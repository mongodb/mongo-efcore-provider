# Native owned-collection `All` predicates + shared negator (EF-335) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an `All` quantifier over an owned (embedded) collection navigation translate natively to a negated `$elemMatch`, via a new shared exact-complement predicate negator that also closes EF-335 (the top-level `All` aggregate with a comparison predicate).

**Architecture:** `All(pred)` ⟺ no element satisfies `¬pred` ⟺ `{path: {$not: {$elemMatch: ¬pred}}}`, so the existing `MongoElemMatchExpression` node is reused with `Negated: true` and **no new AST node is added**. The entire correctness burden collapses onto one new pure component, `MongoExpressionNegator`, whose contract is *exact set complement or decline*. Its central rule: `$eq`/`$ne` may be inverted (they partition every BSON value, including missing and null), while the four relational operators must be `$not`-wrapped (`{f: {$not: {$gt: v}}}`) because `$gt`/`$lte` do **not** partition — neither matches a missing or null field.

**Tech Stack:** C# / .NET (net8.0 for EF8+EF9, net10.0 for EF10), EF Core 8/9/10, MongoDB C# driver, xUnit (plain `Assert.*` — FluentAssertions is not referenced by the test projects).

**Spec:** `docs/superpowers/specs/2026-07-27-native-owned-collection-all-design.md` (committed as `a265e7b`).

## Global Constraints

- Branch `EF-322-owned-collection-all-native`, stacked on the native tip `791037b` (`origin/NativeQueryOngoing`). Do **not** rebase or force-push.
- All new types are `internal`. `src/` is `<Nullable>enable</Nullable>` — annotate accordingly.
- **No `#if` guards.** EF8/EF9/EF10 behavior must be identical; nothing in this slice touches version-conditional surface.
- Preserve file BOMs. Match the surrounding comment density and idiom — these files carry heavy explanatory comments by convention, and that convention is load-bearing here (each guard's comment records *why* it exists).
- Tests use plain xUnit `Assert.*`. Functional tests run serially; each gets a uniquely-named collection.
- **Verification bar for a guard:** prove it has teeth by deleting the guard and watching a test go red. Three tests in the preceding `Any` slice initially passed with the guard they nominally tested removed.
- Not a break per `AGENTS.md`'s rubric (fallback→native, results unchanged, MQL not contract) — **but** see spec §6: a wrong complement *would* change results, which the carve-out does not cover.
- Build one EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Test: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- Run tests with **both `MONGODB_URI` and `ATLAS_URI` unset** so TestContainers gives this run its own isolated `mongodb/mongodb-atlas-local` container.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/…/Query/NativeTranslation/MongoExpressionNegator.cs` | **New.** Pure exact-complement negation of a translated `MongoExpression`. No entity-type or scope knowledge, no state. |
| `src/…/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` | **Modify.** One new `RenderUnary` arm (`Not` over a query-native comparison → `$not` over the operator document); matching `IsQueryDialectRenderable` arm; `IsQueryNativeComparison` becomes `internal static` so the negator shares the one definition. |
| `src/…/Query/NativeTranslation/MongoExpressionTranslator.cs` | **Modify.** `TryMatchAnyMethod` → `TryMatchQuantifierMethod` (adds `All`); the quantifier arm negates for `All`. |
| `src/…/Query/NativeTranslation/NativeCardinalityBinder.cs` | **Modify.** EF-335: negate via the negator instead of `Expression.Not` + translate. |
| `src/…/Query/AGENTS.md` | **Modify.** As-built note. |
| `tests/…/UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs` | **New.** One case per negation rule, every decline, and the two domain invariants. |
| `tests/…/UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs` | **Modify.** New `$not` render tests; **one existing test flips** (see Task 1 Step 6). |
| `tests/…/UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` | **Modify.** `All` translation + declines. |
| `tests/…/FunctionalTests/Query/NativeOwnedCollectionAllTests.cs` | **New.** State matrix, two seeds, MQL assertions, nested quantifiers, declines, and the differential matrix test. |
| `tests/…/FunctionalTests/Query/NativeCardinalityTests.cs` | **Modify.** EF-335 top-level `All` coverage. |

`MongoFieldPrefixRewriter`, the lowerer, the pipeline factory, the shapers, `StreamingEligibility`, and the array-path resolver `TryResolveOwnedCollectionPath` all need **no changes** — a negated element predicate is still element-relative, so the rewriter's existing "prefix `ArrayPath` only" case remains correct.

---

## Task 0: Slice-0 throwaway de-risking spike (the spec §8 gate)

**This task writes NO production code that survives.** It ends with `git checkout .` and a findings doc. Per the project's spike-first practice, each of the last three slices had a plan-invalidating surprise caught this way.

**Files:**
- Create: `.superpowers/sdd/2026-07-27-native-owned-collection-all/spike-findings.md` (gitignored scratch; do **not** commit it — shipped commits carry `docs/superpowers/{specs,plans}` but no `.superpowers/`)
- Scratch only, reverted: a temporary xUnit test file under `tests/…/FunctionalTests/Query/`

**Interfaces:**
- Consumes: nothing.
- Produces: a GO / NARROW verdict that Tasks 1–6 depend on, plus the confirmed answer to each item below.

- [ ] **Step 1: Answer the load-bearing question first — is `$not` over an operator document legal inside `$elemMatch`?**

Write a throwaway test that inserts raw documents and runs a raw aggregate (no EF), so the answer is about the *server*, not the provider:

```csharp
var coll = database.MongoDatabase.GetCollection<BsonDocument>("spike_not_in_elemmatch");
coll.InsertMany(new[]
{
    new BsonDocument { { "Title", "hasBig" },   { "Posts", new BsonArray { new BsonDocument { { "Rank", 9 } } } } },
    new BsonDocument { { "Title", "hasSmall" }, { "Posts", new BsonArray { new BsonDocument { { "Rank", 1 } } } } },
    new BsonDocument { { "Title", "missing" },  { "Posts", new BsonArray { new BsonDocument { { "Other", 1 } } } } },
    new BsonDocument { { "Title", "null" },     { "Posts", new BsonArray { new BsonDocument { { "Rank", BsonNull.Value } } } } },
});

var filter = BsonDocument.Parse("{ Posts: { $elemMatch: { Rank: { $not: { $gt: 5 } } } } }");
var got = coll.Aggregate<BsonDocument>(new[] { new BsonDocument("$match", filter) })
    .ToList().Select(d => d["Title"].AsString).OrderBy(t => t).ToList();
// Record the ACTUAL result. Expected if $not is legal and exact: hasSmall, missing, null (NOT hasBig).
```

Expected: legal, and the three non-`hasBig` rows come back. **If this errors, the gate fires** — go to Step 7.

- [ ] **Step 2: Confirm the exactness claims live, per operator and per element state**

For each operator in `{$eq, $ne, $lt, $lte, $gt, $gte}`, run both `{Posts: {$elemMatch: {Rank: {<op>: 5}}}}` and its `$not`-wrapped form against elements whose `Rank` is `1`, `5`, `9`, **missing**, **explicit null**, and **a string** (`"7"`). Record a table of which rows each form returns, and assert per operator that **the two forms partition the row set** (every row is returned by exactly one of them). Separately confirm `$eq`/`$ne` also partition — that is the claim licensing mirroring rather than `$not`-wrapping for that pair.

- [ ] **Step 3: Dump EF's expression tree for `All` in a predicate**

Mirror the `Any` slice's probe: log `ToString()` and the node types for `Where(b => b.Posts.All(p => p.Rank > 5))`, a conjunctive predicate, a nested `All`-in-`Any`, and the owned-ref-hop form (`b.Home.Notes.All(...)`). Confirm the shape is `Queryable.All(Call(AsQueryable, [<hop chain>]), Quote(lambda))`, that `Method.DeclaringType` is `Queryable` (not `Enumerable`), and that only a 2-arg form exists. Also confirm `AllAnyToContainsRewritingExpressionVisitor` rewrites `All(x => x != c)` into `!Contains(c)` upstream for a primitive-element collection (`b.Tags.All(t => t != "x")`), so those shapes never reach the matcher.

- [ ] **Step 4: Does EF normalize `!(a > b)` into `a <= b`?**

Log the translated predicate for `Where(b => !(b.Rank > 1))`. This decides whether spec §6's flip 3 is reachable and therefore whether it needs functional coverage (Task 3) or only a recorded note.

- [ ] **Step 5: Enumerate the Northwind spec flips and the driver's `All` rendering**

Run the EF10 spec suite under `NativeOnly` and diff against the recorded baseline (`Native` 4589 pass / 0 fail / 19 skip; `NativeOnly` 2192 pass / 2397 fail / 19 skip, atlas-local) — but with the negator **not yet built**, this run only re-establishes the baseline on this branch. Then grep the spec suite for `All(` overrides and list which assert MQL, so Task 6 knows the re-baselining surface up front:

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~SpecificationTests" 2>&1 | tail -30
grep -rn "\.All(" tests/MongoDB.EntityFrameworkCore.SpecificationTests/ | head -40
```

Also record what the driver's LINQ v3 provider emits for an owned-collection `All` under `MongoQueryMode.DriverLinq` (expected `$allElementsTrue` under `$expr`) and whether it throws `MongoCommandException` on the missing/null-array rows — this is spec §8 item 8 and it fixes exactly which cases have a driver oracle in Tasks 4–5.

- [ ] **Step 6: Write the findings doc**

Record each item's answer, the Step 2 partition table verbatim, and a **GO** or **NARROW** verdict. Where a finding contradicts the spec, say so explicitly and name the spec section.

- [ ] **Step 7: Gate**

If Step 1 failed: narrow the slice to the operators whose complement needs no `$not` wrapper (`==`/`!=`, `$in`, regex, nested quantifiers, bare bool), **decline relational comparisons**, update spec §3.1 and §6, and stop for user review before Task 1. If any Step 2 row fails to partition, that operator is declined rather than supported.

- [ ] **Step 8: Revert all scratch code**

```bash
git checkout . && git status --short   # must be clean; the findings doc is gitignored
```

**STOP for user review.** Report the verdict and any spec contradiction before any production code is written.

---

## Task 1: `MongoExpressionNegator` + the renderer's `$not` arm

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionNegator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (`IsQueryNativeComparison` at `:98`, `RenderUnary` at `:136-151`, `IsQueryDialectRenderable` at `:294-314`)
- Create: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs`

**Interfaces:**
- Consumes: `MongoExpression` and subtypes (`MongoBinaryExpression{Operator,Left,Right}`, `MongoUnaryExpression{Operator,Operand}`, `MongoInExpression{Field,Values,Negated}`, `MongoRegexExpression{Field,Kind,Term,Negated}`, `MongoElemMatchExpression{ArrayPath,ElementPredicate,Negated}`, `MongoFieldExpression{Property,ElementName}`); enums `MongoBinaryOperator{Equal,NotEqual,LessThan,LessThanOrEqual,GreaterThan,GreaterThanOrEqual,AndAlso,OrElse,Add,Subtract,Multiply,Divide,Modulo}`, `MongoUnaryOperator{Not}`.
- Produces:
  - `internal static bool MongoExpressionNegator.TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)` — used by Tasks 2 and 3.
  - `internal static bool MongoQueryLanguageRenderer.IsQueryNativeComparison(MongoBinaryExpression b)` — visibility widened from `private`.

- [ ] **Step 1: Write the failing negator tests**

Create `MongoExpressionNegatorTests.cs`, copying the licence header and the `GetPostProperty` helper idiom from `MongoQueryLanguageRendererTests.cs:41-66` (same private `Blog`/`Post` fixture classes: `Blog { ObjectId Id; string Title; List<Post> Posts; }`, `Post { string Heading; int Rank; }`).

```csharp
    private static MongoBinaryExpression Comparison(MongoBinaryOperator op, int value = 5)
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        return new MongoBinaryExpression(
            op, new MongoFieldExpression(rank, "Rank"), new MongoConstantExpression(value, rank));
    }

    private static BsonValue RenderOf(MongoExpression node)
        => new MongoQueryLanguageRenderer().Render(node, new PlaceholderTable());

    [Fact]
    public void Equality_is_inverted_not_wrapped_because_eq_and_ne_partition()
    {
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(MongoBinaryOperator.Equal), out var negated));
        var binary = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(MongoBinaryOperator.NotEqual, binary.Operator);
        Assert.Equal(BsonDocument.Parse("{ Rank: { $ne: 5 } }"), RenderOf(negated));
    }

    [Fact]
    public void Inequality_is_inverted_back_to_equality()
    {
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(MongoBinaryOperator.NotEqual), out var negated));
        Assert.Equal(MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(negated).Operator);
        Assert.Equal(BsonDocument.Parse("{ Rank: 5 }"), RenderOf(negated));
    }

    [Theory]
    [InlineData(MongoBinaryOperator.LessThan, "$lt")]
    [InlineData(MongoBinaryOperator.LessThanOrEqual, "$lte")]
    [InlineData(MongoBinaryOperator.GreaterThan, "$gt")]
    [InlineData(MongoBinaryOperator.GreaterThanOrEqual, "$gte")]
    public void Relational_operators_are_not_wrapped_never_inverted(MongoBinaryOperator op, string mql)
    {
        // The whole safety argument of this slice: $gt and $lte do NOT partition the value space (neither
        // matches a missing or null field), so inverting them would report All == true for a document whose
        // element lacks the field, where LINQ says false. $not over the operator document IS the exact
        // complement. Deleting the $not-wrap in favour of an inversion must make this test red.
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(op), out var negated));
        var unary = Assert.IsType<MongoUnaryExpression>(negated);
        Assert.Equal(MongoUnaryOperator.Not, unary.Operator);
        Assert.Equal(BsonDocument.Parse($"{{ Rank: {{ $not: {{ {mql}: 5 }} }} }}"), RenderOf(negated));
    }

    [Fact]
    public void Conjunction_becomes_a_disjunction_of_complements_de_morgan()
    {
        var and = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Comparison(MongoBinaryOperator.Equal, 1),
            Comparison(MongoBinaryOperator.GreaterThan, 2));

        Assert.True(MongoExpressionNegator.TryNegate(and, out var negated));
        Assert.Equal(MongoBinaryOperator.OrElse, Assert.IsType<MongoBinaryExpression>(negated).Operator);
        Assert.Equal(
            BsonDocument.Parse("{ $or: [ { Rank: { $ne: 1 } }, { Rank: { $not: { $gt: 2 } } } ] }"),
            RenderOf(negated));
    }

    [Fact]
    public void Disjunction_becomes_a_conjunction_of_complements_de_morgan()
    {
        var or = new MongoBinaryExpression(
            MongoBinaryOperator.OrElse,
            Comparison(MongoBinaryOperator.Equal, 1),
            Comparison(MongoBinaryOperator.Equal, 2));

        Assert.True(MongoExpressionNegator.TryNegate(or, out var negated));
        Assert.Equal(MongoBinaryOperator.AndAlso, Assert.IsType<MongoBinaryExpression>(negated).Operator);
    }

    [Fact]
    public void In_flips_to_nin()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var inExpr = new MongoInExpression(
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(new[] { 1, 2 }, rank),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(inExpr, out var negated));
        Assert.True(Assert.IsType<MongoInExpression>(negated).Negated);
        Assert.Equal(BsonDocument.Parse("{ Rank: { $nin: [1, 2] } }"), RenderOf(negated));
    }

    [Fact]
    public void Regex_flips_negated()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var regex = new MongoRegexExpression(
            new MongoFieldExpression(heading, "Heading"),
            MongoRegexKind.StartsWith,
            new MongoConstantExpression("a", heading),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(regex, out var negated));
        Assert.True(Assert.IsType<MongoRegexExpression>(negated).Negated);
    }

    [Fact]
    public void ElemMatch_flips_negated_so_a_nested_quantifier_composes()
    {
        var elemMatch = new MongoElemMatchExpression(
            "Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(elemMatch, out var negated));
        Assert.True(Assert.IsType<MongoElemMatchExpression>(negated).Negated);
    }

    [Fact]
    public void Bare_Any_elem_match_flips_to_exists_false()
    {
        var bareAny = new MongoElemMatchExpression("Comments", elementPredicate: null, negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(bareAny, out var negated));
        Assert.Equal(BsonDocument.Parse("{ 'Comments.0': { $exists: false } }"), RenderOf(negated));
    }

    [Fact]
    public void Double_negation_returns_the_inner_node()
    {
        var inner = Comparison(MongoBinaryOperator.GreaterThan);
        var not = new MongoUnaryExpression(MongoUnaryOperator.Not, inner);

        Assert.True(MongoExpressionNegator.TryNegate(not, out var negated));
        Assert.Same(inner, negated);
    }

    [Fact]
    public void Field_to_field_comparison_declines()
    {
        // No query-dialect rendering ⇒ no query-dialect COMPLEMENT. This must decline in the negator itself,
        // not downstream: mirroring Equal→NotEqual here would produce a node RenderNode sends to the $expr
        // catch-all, and $expr inside $elemMatch is a HARD SERVER ERROR — an execution-time throw rather than
        // a clean fallback.
        var rank = GetPostProperty(nameof(Post.Rank));
        var fieldToField = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Rank"));

        Assert.False(MongoExpressionNegator.TryNegate(fieldToField, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Arithmetic_node_declines()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var arithmetic = new MongoBinaryExpression(
            MongoBinaryOperator.Add,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(1, rank));

        Assert.False(MongoExpressionNegator.TryNegate(arithmetic, out _));
    }

    [Fact]
    public void A_declining_child_declines_the_whole_conjunction_with_no_partial_output()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var and = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Comparison(MongoBinaryOperator.Equal, 1),
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Rank")));

        Assert.False(MongoExpressionNegator.TryNegate(and, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Every_successful_negation_is_query_dialect_renderable_and_renders_without_expr()
    {
        // THE OUTPUT-DOMAIN INVARIANT. This negation is emitted inside $elemMatch, where $expr is a hard
        // server error, so a negator that produced a node the renderer sent to the $expr catch-all would make
        // the whole query throw at execution time under Native as well as NativeOnly.
        var rank = GetPostProperty(nameof(Post.Rank));
        var heading = GetPostProperty(nameof(Post.Heading));
        MongoExpression[] inputs =
        [
            Comparison(MongoBinaryOperator.Equal),
            Comparison(MongoBinaryOperator.NotEqual),
            Comparison(MongoBinaryOperator.LessThan),
            Comparison(MongoBinaryOperator.LessThanOrEqual),
            Comparison(MongoBinaryOperator.GreaterThan),
            Comparison(MongoBinaryOperator.GreaterThanOrEqual),
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.GreaterThan, 2)),
            new MongoBinaryExpression(MongoBinaryOperator.OrElse, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.Equal, 2)),
            new MongoInExpression(new MongoFieldExpression(rank, "Rank"), new MongoConstantExpression(new[] { 1 }, rank), negated: false),
            new MongoRegexExpression(new MongoFieldExpression(heading, "Heading"), MongoRegexKind.Contains, new MongoConstantExpression("a", heading), negated: false),
            new MongoElemMatchExpression("Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false),
            new MongoElemMatchExpression("Comments", elementPredicate: null, negated: false),
            new MongoUnaryExpression(MongoUnaryOperator.Not, Comparison(MongoBinaryOperator.GreaterThan)),
        ];

        foreach (var input in inputs)
        {
            Assert.True(MongoExpressionNegator.TryNegate(input, out var negated), $"failed to negate {input.GetType().Name}");
            Assert.True(
                MongoQueryLanguageRenderer.IsQueryDialectRenderable(negated),
                $"negation of {input.GetType().Name} is not query-dialect renderable");
            var rendered = RenderOf(negated).AsBsonDocument;
            Assert.False(rendered.Contains("$expr"), $"negation of {input.GetType().Name} rendered $expr");
        }
    }

    [Fact]
    public void Negation_is_an_involution_on_the_supported_set()
    {
        // ¬¬X must render identically to X. A rule that is not an exact complement generally fails this.
        MongoExpression[] inputs =
        [
            Comparison(MongoBinaryOperator.Equal),
            Comparison(MongoBinaryOperator.GreaterThan),
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.GreaterThan, 2)),
            new MongoElemMatchExpression("Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false),
        ];

        foreach (var input in inputs)
        {
            Assert.True(MongoExpressionNegator.TryNegate(input, out var once));
            Assert.True(MongoExpressionNegator.TryNegate(once, out var twice));
            Assert.Equal(RenderOf(input), RenderOf(twice));
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: FAIL to compile — `MongoExpressionNegator` does not exist, and `IsQueryNativeComparison` is inaccessible.

- [ ] **Step 3: Widen `IsQueryNativeComparison` and add the renderer's `$not` arm**

In `MongoQueryLanguageRenderer.cs`, change the accessor at `:98` and extend its comment:

```csharp
    // Widened from private to internal so MongoExpressionNegator can share the ONE definition of
    // "query-native" rather than duplicating it — the negator must decline any comparison this returns false
    // for, because such a node has no query-dialect complement (see MongoExpressionNegator.TryNegate).
    internal static bool IsQueryNativeComparison(MongoBinaryExpression b)
        => b.Left is MongoFieldExpression && b.Right is MongoConstantExpression or MongoParameterExpression;
```

Replace `RenderUnary` (`:136-151`) with:

```csharp
    private BsonDocument RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException(
                $"Unsupported unary operator '{unary.Operator}'.");

        // !<query-native comparison> → { field: { $not: { <op>: value } } }.
        //
        // $not over an OPERATOR DOCUMENT is the exact set complement of that operator document — including
        // documents where the field is missing or explicitly null. That exactness is why
        // MongoExpressionNegator $not-wraps the four relational operators instead of inverting them:
        // neither { $gt: 5 } nor { $lte: 5 } matches a missing field, so the pair does NOT partition the
        // value space and an inversion would silently mis-answer All() for such a document.
        if (unary.Operand is MongoBinaryExpression comparison && IsQueryNativeComparison(comparison))
        {
            // Reuse RenderComparison so element naming and value serialization are identical to the
            // un-negated form (a parameter still records a placeholder in the shared table).
            var element = RenderComparison(comparison, placeholders).GetElement(0);

            // RenderComparison emits Equal as a BARE { field: value }; every other operator emits
            // { field: { $op: value } }. Only the latter is already an operator document — check for a
            // leading '$' rather than assuming, so an equality against a document-valued property is
            // wrapped as { $eq: … } instead of being mistaken for one.
            //
            // THIS WRAP IS MANDATORY, NOT DEFENSIVE (spike-measured): { field: { $not: <bareValue> } } is a
            // HARD SERVER ERROR — "$not argument must be a regex or an object". It is reachable in practice
            // through !(x.A == 1), which EF does NOT normalize away. Emitting the bare form would fail every
            // such query at execution time, in every mode.
            var body = element.Value is BsonDocument candidate
                && candidate.ElementCount > 0
                && candidate.GetElement(0).Name.StartsWith('$')
                    ? candidate
                    : new BsonDocument("$eq", element.Value);

            return new BsonDocument(element.Name, new BsonDocument("$not", body));
        }

        if (unary.Operand is not MongoFieldExpression field)
            throw new NativeTranslationNotSupportedException(
                "MongoQueryLanguageRenderer only supports Not over a MongoFieldExpression or a query-native comparison.");

        // !boolProperty → { field: { $ne: true } }
        // (Matches driver-LINQ rendering; also matches missing/null-field semantics.)
        var trueValue = MongoValueRenderer.RenderValue(
            new MongoConstantExpression(true, field.Property), placeholders);
        return new BsonDocument(field.ElementName, new BsonDocument("$ne", trueValue));
    }
```

In `IsQueryDialectRenderable` (`:294-314`), replace the single `MongoUnaryExpression` arm with:

```csharp
            // RenderUnary supports Not over a bare field, and over a QUERY-NATIVE comparison; it throws for
            // anything else (e.g. Not over a conjunction, or over a field-to-field comparison).
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoBinaryExpression cmp }
                => IsQueryNativeComparison(cmp),
```

- [ ] **Step 4: Create `MongoExpressionNegator.cs`**

Use the standard licence header from any sibling file.

```csharp
using System.Diagnostics.CodeAnalysis;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Produces the EXACT logical complement of a translated predicate, or declines.
/// </summary>
/// <remarks>
/// <para>
/// Used to translate a universal quantifier: <c>All(pred)</c> is true exactly when NO element satisfies
/// <c>¬pred</c>, so it renders as a negated <c>$elemMatch</c> over the complement
/// (<c>MongoExpressionTranslator</c>'s quantifier arm), and to negate a top-level <c>All</c> aggregate's
/// predicate into a <c>$match</c> conjunct (<c>NativeCardinalityBinder</c>).
/// </para>
/// <para>
/// <b>The contract is EXACT complement or decline — never an approximation.</b> A predicate whose complement
/// is merely close returns WRONG ROWS rather than falling back, under the default <c>Native</c> mode, which is
/// the one failure mode the provider's not-a-breaking-change rubric does not cover.
/// </para>
/// <para>
/// <b>Why relational operators are <c>$not</c>-wrapped but <c>$eq</c>/<c>$ne</c> are inverted.</b> MongoDB's
/// relational operators are type-bracketed and do not match a missing or null field, so
/// <c>{f: {$gt: 5}}</c> and <c>{f: {$lte: 5}}</c> do NOT partition the value space — an element with no
/// <c>f</c> is matched by neither. Inverting them would make <c>All(p =&gt; p.Rank &gt; 5)</c> report
/// <see langword="true"/> for a document containing an element with no <c>Rank</c>, where LINQ evaluates
/// <c>null &gt; 5</c> as <see langword="false"/>. Wrapping in <c>$not</c> yields the exact complement instead.
/// <c>$eq</c>/<c>$ne</c> DO partition every BSON value including missing and null, so for that one pair
/// inversion is exact — and it keeps the common case rendering as idiomatic <c>{f: {$ne: v}}</c>.
/// </para>
/// <para>
/// <b>Domain invariants, both pinned by <c>MongoExpressionNegatorTests</c>:</b> the admitted input set is a
/// subset of <see cref="MongoQueryLanguageRenderer.IsQueryDialectRenderable"/>'s (enforced directly, by
/// gating on it), and every node produced is itself query-dialect renderable — it never routes to the
/// <c>$expr</c> catch-all, which inside <c>$elemMatch</c> is a hard server error rather than a slow path.
/// </para>
/// </remarks>
internal static class MongoExpressionNegator
{
    /// <summary>
    /// Attempts to build the exact logical complement of <paramref name="node"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the complement, or <see langword="false"/> with no output when
    /// <paramref name="node"/> has no exact query-dialect complement (the caller must then decline, so the
    /// query falls back to driver-LINQ).
    /// </returns>
    public static bool TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        // A node with no query-dialect rendering has no query-dialect COMPLEMENT either. Gating here makes
        // the output-domain invariant unconditional and makes every "not query-native" decline (field-to-
        // field comparison, arithmetic, a parameterized regex term, an unsupported $in values node) fall out
        // of one check instead of being re-derived per case.
        if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
            return false;

        return TryNegateCore(node, out negated);
    }

    private static bool TryNegateCore(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        switch (node)
        {
            // De Morgan. Recurses; a declining child declines the whole tree with no partial output.
            //
            // Producing an $or/$and ARRAY of negated conjuncts (rather than wrapping the conjunction in a
            // single Not node) is MANDATORY, not stylistic: the server rejects { $not: { $or: [...] } } with
            // "unknown operator: $or" (spike-measured). IsQueryDialectRenderable independently refuses a Not
            // over a conjunction, so the illegal form cannot be built — but the reason it must not be is here.
            case MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } and:
            {
                if (!TryNegateCore(and.Left, out var left) || !TryNegateCore(and.Right, out var right))
                    return false;
                negated = new MongoBinaryExpression(MongoBinaryOperator.OrElse, left, right);
                return true;
            }

            case MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } or:
            {
                if (!TryNegateCore(or.Left, out var left) || !TryNegateCore(or.Right, out var right))
                    return false;
                negated = new MongoBinaryExpression(MongoBinaryOperator.AndAlso, left, right);
                return true;
            }

            // A comparison. The IsQueryNativeComparison guard is redundant given TryNegate's gate above, but
            // is kept explicit because this is the one case where getting it wrong is silent wrong data.
            case MongoBinaryExpression comparison
                when MongoQueryLanguageRenderer.IsQueryNativeComparison(comparison):
            {
                switch (comparison.Operator)
                {
                    // $eq and $ne partition every BSON value (including missing/null) — inversion is exact.
                    case MongoBinaryOperator.Equal:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.NotEqual, comparison.Left, comparison.Right);
                        return true;

                    case MongoBinaryOperator.NotEqual:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.Equal, comparison.Left, comparison.Right);
                        return true;

                    // Relational operators do NOT partition — wrap, never invert. See the class remarks.
                    case MongoBinaryOperator.LessThan:
                    case MongoBinaryOperator.LessThanOrEqual:
                    case MongoBinaryOperator.GreaterThan:
                    case MongoBinaryOperator.GreaterThanOrEqual:
                        negated = new MongoUnaryExpression(MongoUnaryOperator.Not, comparison);
                        return true;

                    // An arithmetic operator is not a predicate; nothing to complement.
                    default:
                        return false;
                }
            }

            case MongoInExpression inExpr:
                // $nin is defined as the complement of $in.
                negated = new MongoInExpression(inExpr.Field, inExpr.Values, !inExpr.Negated);
                return true;

            case MongoRegexExpression regex:
                // The renderer negates via an enclosing $not, an exact complement.
                negated = new MongoRegexExpression(regex.Field, regex.Kind, regex.Term, !regex.Negated);
                return true;

            case MongoElemMatchExpression elemMatch:
                // $not complements the $elemMatch; the bare Any() form flips $exists. This is what makes a
                // nested quantifier compose in either order (All-in-Any, Any-in-All, All-in-All).
                negated = new MongoElemMatchExpression(
                    elemMatch.ArrayPath, elemMatch.ElementPredicate, !elemMatch.Negated);
                return true;

            case MongoUnaryExpression { Operator: MongoUnaryOperator.Not } not:
                // Double negation. Exact for any operand, and the operand is renderable by TryNegate's gate
                // (IsQueryDialectRenderable admits a Not only over a bare field or a query-native comparison).
                negated = not.Operand;
                return true;

            case MongoFieldExpression field
                when field.Property.ClrType == typeof(bool) && !field.Property.IsNullable:
                // Complement of a bare-bool predicate { f: true } is { f: { $ne: true } }. Restricted to a
                // non-nullable bool to mirror the translator's own bare-bool acceptance set.
                negated = new MongoUnaryExpression(MongoUnaryOperator.Not, field);
                return true;

            default:
                return false;
        }
    }
}
```

- [ ] **Step 5: Run the negator tests to verify they pass**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoExpressionNegatorTests"`
Expected: PASS, all cases.

- [ ] **Step 6: Handle the KNOWN test flip in the renderer tests**

`MongoQueryLanguageRendererTests.IsQueryDialectRenderable_rejects_Not_over_a_non_field_operand` (`:636-649`) builds `Not(Equal(field, constant))` and asserts `False`. That is now **`True`** by design. Do **not** delete the test — repurpose it and add back genuine negatives, so the classifier's real boundary stays pinned:

```csharp
    [Fact]
    public void IsQueryDialectRenderable_accepts_Not_over_a_query_native_comparison()
    {
        // Flipped by the owned-collection All slice: RenderUnary now renders this as
        // { Rank: { $not: { $eq: 2 } } }, the exact complement. Previously it threw, so the classifier
        // correctly rejected it.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(2, rank)));

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
        Assert.Equal(
            BsonDocument.Parse("{ Rank: { $not: { $eq: 2 } } }"),
            new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }

    [Fact]
    public void IsQueryDialectRenderable_still_rejects_Not_over_a_field_to_field_comparison()
    {
        // RenderUnary's new arm is gated on IsQueryNativeComparison, so this still throws and the
        // classifier must still reject it. Deleting that gate must make this test red.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Other")));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }

    [Fact]
    public void IsQueryDialectRenderable_still_rejects_Not_over_a_conjunction()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var cmp = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(1, rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, cmp, cmp));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }
```

The `Post` fixture in this file (`:54-58`) has no `Other` property — add `public int Other { get; set; }` to it.

Also add a relational render test:

```csharp
    [Fact]
    public void Not_over_a_relational_comparison_renders_as_not_over_the_operator_document()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(5, rank)));

        Assert.Equal(
            BsonDocument.Parse("{ Rank: { $not: { $gt: 5 } } }"),
            new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }
```

- [ ] **Step 7: Run the full unit-test project — catch any other cross-class flip**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~UnitTests"`
Expected: PASS. **A `--filter` scoped to one test class is exactly how the EF-347 slice missed a flip** — run the whole unit project here, and if anything else went red, report it rather than "fixing" it silently.

- [ ] **Step 8: Prove the guards have teeth**

For each, make the edit, run the named test, confirm RED, then revert:
1. In the negator, change the relational case to invert (`GreaterThan` → `LessThanOrEqual`) → `Relational_operators_are_not_wrapped_never_inverted` must fail.
2. Remove the `IsQueryDialectRenderable` gate at the top of `TryNegate` → `Field_to_field_comparison_declines` must fail.
3. Remove `IsQueryNativeComparison` from the new `RenderUnary` arm → `IsQueryDialectRenderable_still_rejects_Not_over_a_field_to_field_comparison` must fail.

Record the three confirmations in the commit message.

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionNegator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs
git commit -m "EF-322: add MongoExpressionNegator + \$not-over-comparison rendering"
```

**STOP for user review.**

---

## Task 2: Wire the `All` quantifier into the translator

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (the quantifier arm at `:302-334`; `TryMatchAnyMethod` at `:702-755`)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionNegator.TryNegate` (Task 1); existing private members `TryResolveOwnedCollectionPath(Expression, out string?, out IEntityType?)`, `ReferencesEnclosingScope(Expression, ParameterExpression)`, `UnwrapAsQueryable(Expression)`, `Unwrap(Expression)`, and `public bool TryTranslate(Expression, [NotNullWhen(true)] out MongoExpression? result)`.
- Produces: `MongoElemMatchExpression` with `Negated: true` for an `All` quantifier — consumed by the existing renderer and `MongoFieldPrefixRewriter`, both unchanged.

- [ ] **Step 1: Write the failing translator tests**

Append to `MongoExpressionTranslatorTests.cs`, following that file's existing idiom for building a translator over an owned-collection model:

```csharp
    [Fact]
    public void Owned_collection_All_translates_to_a_negated_elem_match()
    {
        var translated = TranslateBlogPredicate(b => b.Posts.All(p => p.Rank > 5), out var renderer);

        var elemMatch = Assert.IsType<MongoElemMatchExpression>(translated);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        Assert.True(elemMatch.Negated);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Rank: { $not: { $gt: 5 } } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_with_equality_renders_the_ne_complement()
    {
        TranslateBlogPredicate(b => b.Posts.All(p => p.Heading == "x"), out var renderer);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Heading: { $ne: 'x' } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_with_a_conjunction_renders_a_de_morgan_or()
    {
        TranslateBlogPredicate(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "x"), out var renderer);
        Assert.Equal(
            BsonDocument.Parse(
                "{ Posts: { $not: { $elemMatch: { $or: [ { Rank: { $not: { $gt: 5 } } }, { Heading: { $ne: 'x' } } ] } } } }"),
            renderer);
    }

    [Fact]
    public void Negated_owned_collection_All_drops_the_not_wrapper()
    {
        var translated = TranslateBlogPredicate(b => !b.Posts.All(p => p.Rank > 5), out var renderer);

        Assert.False(Assert.IsType<MongoElemMatchExpression>(translated).Negated);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { Rank: { $not: { $gt: 5 } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_over_a_field_to_field_element_predicate_declines()
    {
        // The negator has no exact complement for a field-to-field comparison, so the whole quantifier
        // declines and the query falls back — it must NOT emit an $expr inside $elemMatch, which is a hard
        // server error.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => p.Rank > p.Other)));
    }

    [Fact]
    public void Owned_collection_All_with_a_correlated_element_predicate_declines()
    {
        // Same ReferencesEnclosingScope guard the Any arm relies on: the element-scoped translator resolves
        // members by NAME, and Blog.Title / Post.Title deliberately collide, so without the guard the
        // owner-rooted condition would be silently retargeted at the element.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => b.Title == "x")));
    }
```

Add the two helpers to that file if it has no equivalent (mirror how its existing owned-collection `Any` tests build the model and translator):

```csharp
    private static MongoExpression? TryTranslateBlogPredicate(Expression<Func<Blog, bool>> predicate)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb =>
        {
            mb.Entity<Blog>().OwnsMany(b => b.Posts);
        });
        var entityType = db.Model.FindEntityType(typeof(Blog))!;
        return new MongoExpressionTranslator(entityType).TryTranslate(predicate.Body, out var result)
            ? result
            : null;
    }

    private static MongoExpression TranslateBlogPredicate(
        Expression<Func<Blog, bool>> predicate, out BsonDocument rendered)
    {
        var translated = TryTranslateBlogPredicate(predicate);
        Assert.NotNull(translated);
        rendered = new MongoQueryLanguageRenderer().Render(translated, new PlaceholderTable()).AsBsonDocument;
        return translated;
    }
```

Ensure this file's private `Blog`/`Post` fixture has `Posts`, `Title`, and `Post { Heading, Rank, Other, Title }` (add what is missing, keeping `Post.Title` colliding with `Blog.Title` for the correlation test).

**Note on hand-built vs EF-produced trees:** these tests build the lambda directly, so the quantifier appears as `Enumerable.All`, not the `Queryable.All(AsQueryable(...), Quote(...))` shape EF emits. `TryMatchQuantifierMethod` must accept both (as `TryMatchAnyMethod` already does), and Task 4's functional tests are what prove the **EF-produced** shape. Do not treat a passing unit test here as proof the real shape works.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"`
Expected: FAIL — the `All` cases translate to `null` (no quantifier match), so `Assert.NotNull` fails.

- [ ] **Step 3: Generalize the matcher**

Add above `TryMatchAnyMethod` and rename it. Keep the existing XML doc, extending it for `All`:

```csharp
    /// <summary>Which quantifier <see cref="TryMatchQuantifierMethod"/> matched.</summary>
    private enum MongoQuantifierKind
    {
        /// <summary><c>Any</c> — at least one element satisfies the predicate (or, bare, the array is non-empty).</summary>
        Any,

        /// <summary><c>All</c> — every element satisfies the predicate. Has no parameterless form.</summary>
        All
    }
```

```csharp
    private static bool TryMatchQuantifierMethod(
        MethodCallExpression call,
        out MongoQuantifierKind kind,
        [NotNullWhen(true)] out Expression? source,
        out LambdaExpression? elementLambda)
    {
        kind = MongoQuantifierKind.Any;
        source = null;
        elementLambda = null;

        if (call.Method.Name == nameof(Enumerable.Any))
            kind = MongoQuantifierKind.Any;
        else if (call.Method.Name == nameof(Enumerable.All))
            kind = MongoQuantifierKind.All;
        else
            return false;

        var declaringType = call.Method.DeclaringType;
        if (declaringType != typeof(Enumerable) && declaringType != typeof(Queryable))
            return false;

        switch (call.Arguments.Count)
        {
            case 1:
                // Bare Any() — the array-is-non-empty test. There is no parameterless All overload, so a
                // 1-argument call can only ever be Any; reject anything else rather than silently treating
                // it as a bare existential.
                if (kind is not MongoQuantifierKind.Any)
                    return false;
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            case 2:
            {
                // The Queryable spelling quotes its lambda; the Enumerable spelling does not.
                var argument = call.Arguments[1];
                if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                    argument = quote.Operand;

                if (argument is not LambdaExpression { Parameters.Count: 1 } lambda)
                    return false;

                source = UnwrapAsQueryable(call.Arguments[0]);
                elementLambda = lambda;
                return true;
            }

            default:
                return false;
        }
    }
```

- [ ] **Step 4: Negate for `All` in the quantifier arm**

Replace the arm at `:302-334`. Note the header comment changes from "Existential quantifier" to cover both:

```csharp
            // --- Quantifiers over an owned (embedded) collection: source.Any() / Any(pred) / All(pred) ---

            case MethodCallExpression call
                when TryMatchQuantifierMethod(call, out var quantifier, out var quantifierSource, out var elementLambda):
            {
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType))
                    return null; // not an owned-collection source rooted at the query parameter

                if (elementLambda is null)
                    return new MongoElemMatchExpression(arrayPath, elementPredicate: null, negated: false);

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
            }
```

Update the comment on the `Not` arm's `MongoElemMatchExpression` flip (`:257-262`) to say `!collection.Any(...)`/`!collection.All(...)`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"`
Expected: PASS.

- [ ] **Step 6: Run the whole unit-test project**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~UnitTests"`
Expected: PASS. Report any flip rather than silently adjusting it.

- [ ] **Step 7: Prove the guard has teeth**

Remove the `ReferencesEnclosingScope` check from the arm → `Owned_collection_All_with_a_correlated_element_predicate_declines` must go RED. Revert.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs
git commit -m "EF-322: owned-collection All translates to a negated \$elemMatch"
```

**STOP for user review.**

---

## Task 3: EF-335 — the top-level `All` aggregate uses the negator

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs:131-143`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionNegator.TryNegate` (Task 1); `MongoSelectDefinition.AddPredicateConjunct(MongoExpression)`.
- Produces: nothing new — behavior change only.

- [ ] **Step 1: Write the failing functional tests**

Add to `NativeCardinalityTests.cs`, matching that file's existing idiom for asserting a shape goes native (`MongoQueryMode.NativeOnly` succeeds) plus a value assertion. Use whatever entity/fixture that file already seeds; the shapes to cover:

```csharp
    [Fact]
    public void All_with_a_relational_predicate_goes_native()
    {
        // EF-335: previously the negation translated but RenderUnary threw, so the gate fell back to
        // driver-LINQ (throwing under NativeOnly). The negator now renders { field: { $not: { $gt: v } } }.
        // Asserted under NativeOnly (the only reliable "went native" signal) AND cross-checked against
        // DriverLinq, which has a working oracle for a top-level All over a flat entity.
        AssertAggregateNativeAndParity(q => q.All(e => e.<NumericProp> > <value>));
    }

    [Fact]
    public void All_with_a_conjunctive_predicate_goes_native_via_de_morgan()
    {
        AssertAggregateNativeAndParity(q => q.All(e => e.<NumericProp> > <v1> && e.<StringProp> == "<v2>"));
    }

    [Fact]
    public void All_with_a_disjunctive_predicate_goes_native_via_de_morgan()
    {
        AssertAggregateNativeAndParity(q => q.All(e => e.<NumericProp> > <v1> || e.<StringProp> == "<v2>"));
    }

    [Fact]
    public void All_returning_false_is_still_correct_when_one_row_fails_the_predicate()
    {
        // The presence-only contract: the negated predicate is pushed as a $match, and ANY surviving row
        // means All is false. A predicate no row fails and one exactly one row fails must both be right.
        AssertAggregateNativeAndParity(q => q.All(e => e.<NumericProp> > <valueOneRowFails>));
    }

    [Fact]
    public void All_with_a_field_to_field_predicate_still_falls_back()
    {
        // No exact complement ⇒ the binder declines ⇒ graceful fallback: correct under Native/DriverLinq,
        // throws only under NativeOnly. This is the boundary of what the negator admits.
        AssertAggregateFallsBackGracefully(q => q.All(e => e.<NumericProp> > e.<OtherNumericProp>));
    }

    [Fact]
    public void All_with_a_regex_predicate_is_unchanged_by_the_negator_swap()
    {
        // Already native BEFORE this slice (the Not arm flipped MongoRegexExpression.Negated). Pinned so the
        // swap from Expression.Not to TryNegate is proven behavior-preserving, not just additive.
        AssertAggregateNativeAndParity(q => q.All(e => e.<StringProp>.StartsWith("<prefix>")));
    }
```

If `NativeCardinalityTests.cs` has no such helpers, write them locally in that class, following `NativeOwnedCollectionPredicateTests.AssertNativeAndParity` (`:233-250`) as the model: run under `NativeOnly`, run under `DriverLinq`, assert equal, and return the value. For the fallback helper, assert `Assert.Throws<NativeTranslationNotSupportedException>` under `NativeOnly` and equality between `Native` and `DriverLinq`.

Replace every `<…>` placeholder with the actual property names and values from that file's fixture, choosing values so each assertion is **discriminating** (a predicate that is trivially true for every row proves nothing).

- [ ] **Step 2: Run the tests to verify the right ones fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: the three relational/conjunctive/disjunctive tests FAIL under `NativeOnly` with `NativeTranslationNotSupportedException`; the regex and field-to-field tests PASS already.

- [ ] **Step 3: Swap to the negator**

Replace `NativeCardinalityBinder.cs:131-143`:

```csharp
        if (op is MongoAggregateOperator.All)
        {
            // All(pred) ≡ no row fails pred. Push the EXACT COMPLEMENT of the predicate as a $match; presence
            // of any surviving row (after $count) means at least one row failed pred, so All is false.
            //
            // The complement is built by MongoExpressionNegator over the TRANSLATED tree, not by wrapping the
            // LINQ body in Expression.Not (which is what this did before EF-335). The old form translated a
            // negated comparison into MongoUnaryExpression(Not, comparison) — a node the renderer had no case
            // for, so it threw at RENDER time and the gate silently fell back. Negating after translation also
            // means De Morgan applies, so a conjunctive/disjunctive predicate goes native too.
            if (predicate is null)
                return false;

            if (!translator.TryTranslate(predicate.Body, out var predicateNode))
                return false;

            if (!MongoExpressionNegator.TryNegate(predicateNode, out var negatedNode))
                return false; // no exact complement — decline, so the query falls back to driver-LINQ

            select.AddPredicateConjunct(negatedNode);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: PASS, all six.

- [ ] **Step 5: Cover the `!(comparison)` widening — MANDATORY, and wider than originally scoped**

Task 0 settled this: **EF normalizes nothing** (`!(a > b)`, `!(a == b)`, `!(a && b)` all arrive intact), so the widening is reachable and needs real coverage — and it covers **all six** comparison operators, not just relational ones, because `TranslateNode`'s `Not` arm already builds `MongoUnaryExpression(Not, <comparison>)` for any of them.

Add to `NativeExprComparisonTests.cs` (or `NativeCardinalityTests.cs`, whichever that file's fixture fits better) a theory over all six operators asserting each now goes native under `NativeOnly` **and** equals `DriverLinq`:

```csharp
    [Theory]
    [MemberData(nameof(NegatedComparisonCases))]
    public void Negated_comparison_predicate_goes_native(string name, Expression<Func<TEntity, bool>> predicate)
    {
        // Flip 3 of the All slice: RenderUnary now renders Not over a query-native comparison as
        // { field: { $not: { <op>: value } } }. Previously it threw and the gate fell back. EF does not
        // normalize any of these six forms away (spike-confirmed), so all six are reachable from user code.
        AssertNativeAndParity(q => q.Where(predicate));
    }
```

with cases for `!(x.N > v)`, `!(x.N >= v)`, `!(x.N < v)`, `!(x.N <= v)`, `!(x.N == v)`, `!(x.N != v)`.

**The two equality forms are the important ones** — they are what exercise illegal form 1 (`{field: {$not: <bareValue>}}` is a server error). Verify by temporarily removing the `$`-prefix check from Task 1's `RenderUnary` arm: `!(x.N == v)` must then fail with a `MongoCommandException` mentioning `$not argument must be a regex or an object`. Revert and record.

Also assert `Where(e => !(e.N > v && e.S == s))` still **falls back** (a `Not` over a conjunction is not a comparison, so `IsQueryDialectRenderable` still refuses it) — that boundary is what keeps `Where`-level De Morgan out of scope.

- [ ] **Step 6: Run the whole functional Query subset**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~FunctionalTests.Query"`
Expected: PASS. Any pre-existing test asserting that a top-level `All` **falls back** will flip here — report it; do not delete the assertion, invert it with a value check proving the native path is correct.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs
git commit -m "EF-335: top-level All negates via MongoExpressionNegator, goes native"
```

**STOP for user review.**

---

## Task 4: Functional tests for owned-collection `All`

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs`

**Interfaces:**
- Consumes: the shipped behavior of Tasks 1–2.
- Produces: the `Blog`/`Post`/`Comment`/`Home`/`Note` fixture, `BlogModel`, the row builders, and the assert helpers — **all reused by Task 5's differential test in the same file.**

- [ ] **Step 1: Create the file with the fixture and seeds**

Copy the structure of `NativeOwnedCollectionPredicateTests.cs` (`:1-232`) — licence header, `[XUnitCollection("QueryTests")]`, `IClassFixture<TemporaryDatabaseFixture>`, `CreateContext<T>`, `UniqueCollectionName`, the row builders, and `Seed`. Differences that matter:

```csharp
/// <summary>
/// EF-322: an All quantifier over an OWNED (embedded) collection navigation translates natively to a NEGATED
/// $elemMatch over the exact complement of the element predicate. Each admitted shape asserts a NativeOnly
/// routing proof; each excluded shape asserts a clean decline.
/// </summary>
```

Element properties under test are **nullable** so the fixture can express a missing/null element field — which is the exact state where a wrong complement diverges, and therefore the state the whole slice turns on:

```csharp
    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    private class Post
    {
        // Nullable ON PURPOSE: a missing or explicitly-null stored field must MATERIALIZE (as null) rather
        // than throw, or the missing-field state cannot be exercised at all. A required non-nullable element
        // property with a missing field is a separate, pre-existing materialization concern (it throws in
        // every mode) and is deliberately out of this file's scope.
        public int? Rank { get; set; }
        public string? Heading { get; set; }
        public int? Other { get; set; }

        // DELIBERATELY COLLIDES with Blog.Title so the correlated-element-predicate guard is exercised on an
        // input that would otherwise be ACCEPTED — the element-scoped translator resolves members by NAME.
        public string Title { get; set; } = "";

        public List<Comment> Comments { get; set; } = [];
    }

    private class Comment { public int? Age { get; set; } }
    private class Home { public List<Note> Notes { get; set; } = []; }
    private class Note { public int? Length { get; set; } }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));
        mb.Entity<Blog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));
    };
```

Row builders — each returns a fresh document. **Build the shared rows exactly once** so the full-matrix and well-formed seeds cannot desynchronize (the preceding slice had to fix exactly that):

```csharp
    // Every element satisfies Rank > 5.
    private static BsonDocument AllPassRow()
        => Row("allpass", new BsonArray
        {
            Post(rank: 9, heading: "a"),
            Post(rank: 7, heading: "b"),
        });

    // One element fails Rank > 5 — the discriminating row for All.
    private static BsonDocument OneFailsRow()
        => Row("onefails", new BsonArray
        {
            Post(rank: 9, heading: "a"),
            Post(rank: 1, heading: "b"),
        });

    // An element whose Rank field is ABSENT. THE critical row: naive operator inversion ($gt → $lte) reports
    // All == true here, because neither $gt nor $lte matches a missing field, where LINQ (null > 5 == false)
    // says All == false. Any regression to inversion must make a test on this row fail.
    private static BsonDocument MissingFieldRow()
        => Row("missingfield", new BsonArray { PostWithoutRank(heading: "a") });

    // An element whose Rank is explicitly BSON null — same reasoning as MissingFieldRow.
    private static BsonDocument NullFieldRow()
        => Row("nullfield", new BsonArray { Post(rank: null, heading: "a") });

    private static BsonDocument EmptyPostsRow() => Row("empty", new BsonArray());
    private static BsonDocument MissingPostsRow() => Row("missing", posts: null);
    private static BsonDocument NullPostsRow() => Row("null", BsonNull.Value);

    private static BsonDocument Post(int? rank, string? heading, int? other = 0, string title = "p")
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", other.HasValue ? other.Value : BsonNull.Value },
            { "Title", title },
            { "Comments", new BsonArray() }
        };

    private static BsonDocument PostWithoutRank(string? heading, string title = "p")
        => new()
        {
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", 0 }, { "Title", title }, { "Comments", new BsonArray() }
        };

    // Home/Tags are always seeded present-but-empty: both are separate required properties on Blog, unrelated
    // to what these rows test, and a document missing them fails materialization with an unrelated error the
    // moment a predicate returns the row as a full Blog.
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

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // The full matrix: every element state and every array state that changes $elemMatch semantics.
    private IMongoCollection<Blog> SeedMatrix(string name)
        => Seed(name, AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(),
                      EmptyPostsRow(), MissingPostsRow(), NullPostsRow());

    // Rows whose Posts is a real, non-null ARRAY — element-level missing/null fields are fine here.
    //
    // Spike refinement (measured, and wider than the Any slice's equivalent seed): the driver's own All
    // translation ($expr: {$allElementsTrue: {$map: …}}) aborts the aggregate ONLY on an array-level
    // missing/null Posts — "$allElementsTrue's argument must be an array, but is null". With every array
    // present but ELEMENTS carrying a missing or explicit-null Rank, DriverLinq runs and agrees with both the
    // in-memory oracle and the native MQL. So MissingFieldRow/NullFieldRow BELONG in the parity seed: they put
    // an independent driver cross-check on exactly the element states where a wrong complement shows up.
    // Only the array-level missing/null rows are confined to the NativeOnly-plus-hand-verified leg.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(), EmptyPostsRow());
```

Assert helpers — copy `AssertNativeAndParity` (`:233-250`), `AssertNativeOnlyMatches` (`:307-312`), and `AssertDeclinesCleanly` (`:260-…`) from `NativeOwnedCollectionPredicateTests.cs`, substituting this file's `BlogModel`.

The seed split above is already **adjusted to what Task 0 measured** (element-level missing/null keeps the driver oracle working; only array-level missing/null breaks it) — do not re-narrow it to match the `Any` slice's equivalent seed.

The widening does **not** change any expected-title list in Steps 2–4: for both `Rank > 5` and the `Rank > Other` decline, `missingfield` and `nullfield` evaluate `All` to `false` (a null or missing element field fails every relational comparison), so they are absent from every expectation written against the narrower seed. That was checked when the seed was widened — but re-derive rather than trust it if any assertion disagrees.

- [ ] **Step 2: Write the core shape tests**

```csharp
    [Fact]
    public void Owned_collection_All_goes_native()
    {
        var collection = SeedMatrix(nameof(Owned_collection_All_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));

        // allpass: both elements pass. empty/missing/null: All over an empty sequence is true.
        // missingfield/nullfield: null > 5 is false, so All is FALSE — the rows a naive inversion gets wrong.
        // onefails: one element fails.
        Assert.Equal(new[] { "allpass", "empty", "missing", "null" }, titles);
    }

    [Fact]
    public void Owned_collection_All_matches_driver_linq_on_well_formed_rows()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_matches_driver_linq_on_well_formed_rows));
        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void Negated_owned_collection_All_goes_native()
    {
        var collection = SeedMatrix(nameof(Negated_owned_collection_All_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => !b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "missingfield", "nullfield", "onefails" }, titles);
    }

    [Fact]
    public void Owned_collection_All_over_an_empty_or_absent_array_is_true()
    {
        // Called out separately from the matrix test because it is the semantic most likely to be "fixed"
        // into a regression by someone who reads $not/$elemMatch as "the array must be non-empty".
        var collection = Seed(
            nameof(Owned_collection_All_over_an_empty_or_absent_array_is_true),
            EmptyPostsRow(), MissingPostsRow(), NullPostsRow());

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "empty", "missing", "null" }, titles);
    }

    [Fact]
    public void Owned_collection_All_multi_condition_requires_every_element_to_satisfy_all_conditions()
    {
        var collection = Seed(
            nameof(Owned_collection_All_multi_condition_requires_every_element_to_satisfy_all_conditions),
            // Each element satisfies ONE condition but not both: All must be FALSE. A De Morgan slip that
            // ANDed the complements instead of ORing them would wrongly return this row.
            Row("split", new BsonArray { Post(rank: 9, heading: "no"), Post(rank: 1, heading: "yes") }),
            Row("both", new BsonArray { Post(rank: 9, heading: "yes") }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "yes")));
        Assert.Equal(new[] { "both" }, titles);
    }

    [Fact]
    public void Owned_collection_All_emits_a_negated_elem_match()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_emits_a_negated_elem_match));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        _ = db.Entities.AsNoTracking().Where(b => b.Posts.All(p => p.Rank > 5)).ToList();

        // Pins BOTH levels: the enclosing $not/$elemMatch AND the inner $not over the operator document.
        AssertMql(db, "{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } } } } } }");
    }

    [Fact]
    public void Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        _ = db.Entities.AsNoTracking()
            .Where(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "yes")).ToList();

        AssertMql(db, "{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"$or\" : [{ \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } }, { \"Heading\" : { \"$ne\" : \"yes\" } }] } } } } }");
    }
```

Use whatever MQL-capture helper the sibling functional tests use (`TestMqlLoggerFactory` via the file's own `AssertMql`); copy the exact idiom from `NativeOwnedCollectionPredicateTests.cs`'s MQL test rather than inventing one. **Capture the actual emitted MQL from a first run and paste it in** — do not hand-write the expected string and assume the formatting matches.

- [ ] **Step 3: Write the nesting, owned-ref-hop, and tracking tests**

```csharp
    [Fact]
    public void All_within_Any_goes_native()
    {
        var collection = Seed(nameof(All_within_Any_goes_native),
            Row("hasAllPassing", new BsonArray { PostWithComments("a", new BsonArray { Comment(9), Comment(7) }) }),
            Row("noneAllPassing", new BsonArray { PostWithComments("a", new BsonArray { Comment(9), Comment(1) }) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.All(c => c.Age > 5))));
        Assert.Equal(new[] { "hasAllPassing" }, titles);
    }

    [Fact]
    public void Any_within_All_goes_native()
    {
        var collection = Seed(nameof(Any_within_All_goes_native),
            Row("everyPostHasOne", new BsonArray { PostWithComments("a", new BsonArray { Comment(9) }) }),
            Row("onePostHasNone", new BsonArray
            {
                PostWithComments("a", new BsonArray { Comment(9) }),
                PostWithComments("b", new BsonArray { Comment(1) })
            }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Comments.Any(c => c.Age > 5))));
        Assert.Equal(new[] { "everyPostHasOne" }, titles);
    }

    [Fact]
    public void All_within_All_goes_native()
    {
        var collection = Seed(nameof(All_within_All_goes_native),
            Row("allGood", new BsonArray { PostWithComments("a", new BsonArray { Comment(9), Comment(7) }) }),
            Row("oneBad", new BsonArray { PostWithComments("a", new BsonArray { Comment(9), Comment(1) }) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Comments.All(c => c.Age > 5))));
        Assert.Equal(new[] { "allGood" }, titles);
    }

    [Fact]
    public void All_over_a_collection_reached_through_an_owned_reference_goes_native()
    {
        // Proves the array path is built scope-relatively and composes through an owned single-ref hop:
        // the emitted path must be "Home.Notes", not "Notes".
        var collection = Seed(nameof(All_over_a_collection_reached_through_an_owned_reference_goes_native),
            RowWithNotes("allLong", new BsonArray { Note(9), Note(7) }),
            RowWithNotes("oneShort", new BsonArray { Note(9), Note(1) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Home.Notes.All(n => n.Length > 5)));
        Assert.Equal(new[] { "allLong" }, titles);
    }

    [Fact]
    public void Owned_collection_All_is_correct_for_a_tracking_query()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_is_correct_for_a_tracking_query));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.Where(b => b.Posts.All(p => p.Rank > 5))
            .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }
```

Add the small builders these use (`PostWithComments`, `Comment`, `Note`, `RowWithNotes`) beside the others, following the same shape as `Post`/`Row`.

- [ ] **Step 4: Write the decline tests**

```csharp
    [Fact]
    public void All_with_a_field_to_field_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // The negator has no exact complement for a field-to-field comparison. Proven to decline under
        // NativeOnly AND to produce correct rows via the fallback — a decline is only safe if the path it
        // falls back to actually works.
        var collection = SeedWellFormed(
            nameof(All_with_a_field_to_field_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Rank > p.Other)));
        Assert.Equal(new[] { "allpass", "empty", "onefails" }, titles);
    }

    [Fact]
    public void All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // Post.Title collides with Blog.Title, so a mis-scoped owner-rooted condition would select DIFFERENT
        // rows — which is what makes this decline test discriminating rather than vacuous.
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows),
            Row("match", new BsonArray { Post(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { Post(rank: 9, heading: "a", title: "match") }));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
    }

    [Fact]
    public void Primitive_collection_All_is_rewritten_upstream_and_is_unaffected_by_this_slice()
    {
        // EF Core's own AllAnyToContainsRewritingExpressionVisitor rewrites All(x => x != c) into
        // !Contains(c) BEFORE the native translator sees it, so no All node ever reaches the quantifier
        // matcher for a primitive-element collection. Assert what the pre-existing Contains/$in path
        // ACTUALLY does (per the Task 0 spike), not an assumed decline.
        // ... assert per the spike's recorded finding ...
    }
```

Add an arithmetic-element-predicate decline too (`b.Posts.All(p => p.Rank + 1 > 5)`), following the same pattern.

Then add the **primitive-collection `==` shape the spike found**, which is a genuinely different tree from the `!= c` case the design already discussed:

```csharp
    [Fact]
    public void Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution()
    {
        // Spike finding: Tags.All(t => t == "x") is NOT rewritten by EF's AllAnyToContainsRewriting (which
        // only handles All(x => x != c) / Any(x => x == c)), and it arrives in a shape the Any slice's notes
        // said could not occur — Enumerable.All (not Queryable), a BARE unquoted lambda, and NO AsQueryable()
        // wrapper. The generalized matcher therefore MATCHES it, and it must decline one step later because
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION and Tags is a primitive
        // collection property. Verified: UnwrapAsQueryable passes an unwrapped source through unchanged, so
        // this is a clean decline, not a crash — this test is what keeps it that way.
        var collection = SeedWellFormed(
            nameof(Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.All(t => t == "x")));
    }
```

The `Primitive_collection_All_…` placeholder in the list above now has its assertion — Task 0's finding supplied it, so **no test in this file may ship with a commented-out or absent assertion.**

- [ ] **Step 5: Run the file**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionAllTests"`
Expected: PASS. Every expected-title list above is a **hand-computed prediction** — if one disagrees with reality, work out which is wrong before changing the assertion. A silently "corrected" expectation is how a wrong complement ships.

- [ ] **Step 6: Prove the critical row has teeth**

Temporarily change the negator's relational case to invert (`GreaterThan` → `LessThanOrEqual`). `Owned_collection_All_goes_native` **must** go red on the `missingfield`/`nullfield` rows. This is the single most important teeth-check in the slice: it demonstrates the fixture can actually detect the bug the design exists to prevent. Revert, and record the confirmation in the commit message.

- [ ] **Step 7: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs
git commit -m "EF-322: functional coverage for native owned-collection All"
```

**STOP for user review.**

---

## Task 5: The differential matrix test

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs`

**Interfaces:**
- Consumes: Task 4's fixture, `BlogModel`, row builders, `Seed`/`SeedMatrix`, and `CreateContext`.
- Produces: nothing consumed downstream.

- [ ] **Step 1: Write the differential test**

The design: one `Expression<Func<Blog, bool>>` per case is used **both** as the server query and — compiled — as the in-memory oracle, so the two can never drift. The oracle evaluates over the *materialized* entities, which is definitionally the semantics the provider promises.

```csharp
    // ------------------------------------------------------------------
    // Differential matrix — the primary correctness bar for the negator
    // ------------------------------------------------------------------
    //
    // A mis-negated element predicate returns WRONG ROWS rather than declining, and the driver-LINQ oracle
    // cannot cover the missing/null states (its own All translation aborts the aggregate on such a document).
    // So the oracle here is IN-MEMORY LINQ over the materialized entities: the SAME expression is sent to the
    // server and, compiled, evaluated client-side. Using one expression for both legs is what makes this a
    // real differential test rather than two hand-written predicates that can silently disagree.

    public static TheoryData<string, Expression<Func<Blog, bool>>> AllMatrixCases() => new()
    {
        { "eq",              b => b.Posts.All(p => p.Rank == 9) },
        { "ne",              b => b.Posts.All(p => p.Rank != 9) },
        { "lt",              b => b.Posts.All(p => p.Rank < 5) },
        { "lte",             b => b.Posts.All(p => p.Rank <= 5) },
        { "gt",              b => b.Posts.All(p => p.Rank > 5) },
        { "gte",             b => b.Posts.All(p => p.Rank >= 5) },
        { "and",             b => b.Posts.All(p => p.Rank > 5 && p.Heading == "a") },
        { "or",              b => b.Posts.All(p => p.Rank > 5 || p.Heading == "a") },
        { "not",             b => b.Posts.All(p => !(p.Rank > 5)) },
        { "eq-null",         b => b.Posts.All(p => p.Rank == null) },
        { "ne-null",         b => b.Posts.All(p => p.Rank != null) },
        { "in",              b => b.Posts.All(p => new[] { 7, 9 }.Contains(p.Rank!.Value)) },
        { "startswith",      b => b.Posts.All(p => p.Heading!.StartsWith("a")) },
        { "nested-any",      b => b.Posts.All(p => p.Comments.Any(c => c.Age > 5)) },
        { "nested-all",      b => b.Posts.All(p => p.Comments.All(c => c.Age > 5)) },
        { "negated-all",     b => !b.Posts.All(p => p.Rank > 5) },
        // Any regressions: this path must be completely unaffected by the slice.
        { "any-gt",          b => b.Posts.Any(p => p.Rank > 5) },
        { "any-bare",        b => b.Posts.Any() },
        { "negated-any",     b => !b.Posts.Any(p => p.Rank > 5) },
    };

    [Theory]
    [MemberData(nameof(AllMatrixCases))]
    public void Quantifier_result_equals_the_in_memory_oracle_for_every_element_and_array_state(
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
        // The projection is client-side on purpose — a bare-scalar Select would itself not be native and
        // would throw under NativeOnly for reasons unrelated to the quantifier.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // Every element state crossed with every array state that changes quantifier semantics.
    private static BsonDocument[] DifferentialRows() =>
    [
        AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(),
        EmptyPostsRow(), MissingPostsRow(), NullPostsRow(),
        Row("belowAndAbove", new BsonArray { Post(rank: 1, heading: "a"), Post(rank: 9, heading: "b") }),
        Row("exactBoundary", new BsonArray { Post(rank: 5, heading: "a") }),
        Row("mixedNullAndValue", new BsonArray { Post(rank: null, heading: "a"), Post(rank: 9, heading: "a") }),
        Row("headingNull", new BsonArray { Post(rank: 9, heading: null) }),
        Row("withComments", new BsonArray { PostWithComments("a", new BsonArray { Comment(9), Comment(1) }) }),
        Row("emptyComments", new BsonArray { PostWithComments("a", new BsonArray()) }),
    ];
```

- [ ] **Step 2: Run the differential test**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Quantifier_result_equals_the_in_memory_oracle"`
Expected: PASS for every case.

**If a case fails, that is the test doing its job — do not weaken it.** Diagnose which side is wrong:
- If the server disagrees with LINQ on a **missing/null** row, the complement for that operator is not exact → **decline that operator in the negator** and record it in the spec's non-goals, per spec §8's gate.
- If a case cannot go native at all (throws under `NativeOnly`), either the shape is legitimately out of scope — move it to a decline test and delete the row — or a guard is over-declining.

Some cases are expected **not** to be native and will throw rather than fail an equality: `in` and `startswith` over a nullable element leaf may decline. Where that happens, move the case out of this theory into an explicit decline test rather than leaving it silently absent.

- [ ] **Step 3: Confirm the differential test detects a wrong complement**

Change the negator's `Equal` case to `$not`-wrap instead of inverting (still correct — a sanity check that the harness is not merely insensitive: this must stay GREEN). Then change the relational case to invert (`GreaterThan` → `LessThanOrEqual`); the `gt` case **must** go RED. Then remove De Morgan's operator swap (`OrElse` → `AndAlso` in the `AndAlso` arm); the `and` case **must** go RED. Revert all three and record the outcomes in the commit message.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs
git commit -m "EF-322: differential matrix test for the predicate negator"
```

**STOP for user review.**

---

## Task 6: Spec sweep, flips, docs, and the three-version run

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: whichever specification tests need re-baselining (enumerated by Task 0 Step 5 and confirmed here)

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces: the as-built record.

- [ ] **Step 1: Run the EF10 `NativeOnly` spec sweep and enumerate the delta**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~SpecificationTests" 2>&1 | tail -40
```

Compare against the baseline **Task 0 re-measured on this branch**: `Native` 4582 / 0 / 19 and `NativeOnly` 2185 / 2397 / 19, total **4601**. Note that is **7 tests fewer than `docs/native-query-status-EF-322.md` records** (4608); the failure count matched exactly at 2397 and `Native` had 0 failures, so the spike attributed it to discovery-time collection differences, not breakage. If 4601 reproduces here, **correct the status doc's totals** rather than treating the 7 as broken.

**A nonzero delta is EXPECTED — but from flip 3, not from the `All` aggregate.** Task 0 inverted the spec's original assumption: the EF-335 half contributes **zero** spec delta (`All_top_level` is already native; `All_top_level_column` is field-to-field and stays declined; every other `All`-shaped spec test is unsupported for an unrelated reason). The delta comes from the `RenderUnary` widening making negated comparisons native. Watch list, from the spike:

`Where_complex_negated_expression_optimized`, `Where_de_morgan_and_optimized`, `Where_de_morgan_or_optimized`, `Where_negated_boolean_expression_compared_to_another_negated_boolean_expression`, `Where_not_bool_member_compared_to_binary_expression`, `Where_not_bool_member_compared_to_not_bool_member`, `Where_bool_member_negated_twice`, `Where_bool_client_side_negated`, `Where_ternary_boolean_condition_negated`, `Where_constant_is_not_null`, `Where_null_is_not_null`, `Not_Any_false`, `Where_compare_constructed_multi_value_not_equal` (+ tuple variants).

Enumerate the newly-passing tests by name and confirm each is a genuine negation shape. **A newly-FAILING test is a regression** — stop and investigate, do not re-baseline it away.

- [ ] **Step 2: Re-baseline the spec MQL assertions**

**Already-diagnosed flip, carried in from Task 5 — do this one first.**
`NorthwindAggregateOperatorsQueryMongoTest.Select_All` (both `async` cases) is RED on the branch right now, and it
is an expected re-baseline caused by Task 3's EF-335 swap, NOT a regression. Measured:

```
baseline: Orders.{ "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
actual:   Orders.{ "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$limit" : 1 }
```

The `$match` is byte-identical; only the driver-LINQ fallback's trailing `$project: {_id: 0, _v: null}` stage
disappears, which is the signature of native routing (the native presence-only aggregate needs no projection).
Results are unaffected — the base EF result assertion passes; only `AssertMql` fails. Update the expected string
to the actual (drop the `$project` stage) and add a one-line comment naming this slice and the reason. **The spike
missed it because it only checked the `NativeOnly` pass set, and this test is `NativeOnly`-failing but has a
`Native`-mode MQL baseline — check BOTH axes for every test in the watch list below, not just pass/fail.**

Then two more jobs, of which the first is a **regression check, not a re-baseline**:

1. **`NorthwindMiscellaneousQueryMongoTest.All_top_level` must be byte-identical.** It is already native today, emitting `{$match: {ContactName: {$not: {$regularExpression: {pattern: "^A", options: "s"}}}}}, {$limit: 1}`. The negator flips the same `MongoRegexExpression.Negated` the old `Expression.Not` path did, so its MQL must not change. If it does, the Task 3 swap is not behavior-preserving — investigate rather than accept.
2. **For each flip-3 test whose MQL actually changed**, run it, capture the emitted pipeline, and update the expectation, adding a one-line comment naming this slice as the reason. Per the versioning rubric, changed MQL for a supported query is not a break.

- [ ] **Step 3: Run the full three-version sweep**

Invoke the `/test-all` skill (builds and tests EF8, EF9, EF10 in parallel with isolated containers).
Expected: 0 failures on all three. Record the per-version totals and compare against the pre-slice baseline (EF8 7469 / EF9 7830 / EF10 7427) — the increase should equal the number of tests added, uniformly across versions (this slice adds no `#if`, so a non-uniform delta means something is version-sensitive and needs explaining).

- [ ] **Step 4: Write the `Query/AGENTS.md` as-built note**

Add a note after the existing "Owned-collection `Any` quantifier predicates (EF-322)" note, in that file's established style (bold lead-ins, precise mechanism, explicit deferral list). It must cover:

- The shapes now native: owned-collection `All`, negated `All`, nested quantifiers in either order, the owned-ref-hop form, and the top-level `All` aggregate (EF-335 closed).
- The rendering: `{path: {$not: {$elemMatch: ¬pred}}}`, and why that is correct for empty/missing/null arrays.
- `MongoExpressionNegator`'s **exact-complement-or-decline** contract, and **why `$eq`/`$ne` may be inverted while the four relational operators must be `$not`-wrapped** — with the concrete counter-example (`All(p => p.Rank > 5)` over an element with no `Rank`), since this is the one fact a future editor most needs and is least likely to re-derive.
- The two domain invariants and the now **three-way** sync requirement: `MongoExpressionNegator` ↔ `IsQueryDialectRenderable` ↔ `RenderNode`.
- `IsQueryNativeComparison` widened to `internal` and why the negator must gate on it itself.
- The incidental `Where(!(comparison))` widening (or, per the Task 0 finding, that EF normalizes it away and the widening is unreachable).
- **The index asymmetry with the `Any` slice, as measured (spec §7's table):** root-scope `{f: {$not: {$gt: v}}}` (the EF-335 emission) is **IXSCAN** — index-usable, better than the spec first assumed — while the owned-collection `All` form `{path: {$not: {$elemMatch: {f: {$not: …}}}}}` is a **COLLSCAN**. A deliberate correctness-over-index trade, not an oversight: the index-friendly alternative returns wrong answers. Note the already-shipped `!Any(...)` form is equally a COLLSCAN, so no *new* class of index regression is introduced.
- **The two illegal MQL forms** (`{f: {$not: <bareValue>}}`; `$not` over `$or`/`$and`) and that the `$eq` wrap and the De Morgan array are therefore mandatory rather than stylistic.
- **The primitive-collection `All(t => t == c)` shape** — `Enumerable.All`, bare lambda, no `AsQueryable()` — which contradicts the `Any` note's "always `Queryable`/`Quote`/`AsQueryable`" claim. **Correct that claim in the `Any` note too**, since it is now known to hold only for owned *entity* collections.
- The risk-profile note from spec §6: this slice *could* have changed results, which is why the differential test exists.
- Deferrals, verbatim from spec §7.

Also correct the `Any` note's "**Deferred/still falls back:** `All` …" clause, which is now stale.

- [ ] **Step 5: Verify the docs match the build**

Re-read the new note against the shipped code. Every mechanism claim must be checkable against a named file/method. Delete any sentence that cannot be.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md tests/
git commit -m "EF-322: re-baseline spec MQL, AGENTS.md as-built note for owned-collection All"
```

**STOP for user review** — then squash the branch to a single commit per the stacked-PR workflow, keeping a `-presquash` backup branch, and verify the squashed tree is byte-identical (`git diff <squashed> <presquash>` empty) before a plain fast-forward push.

---

## Self-Review

**1. Spec coverage.**

| Spec section | Task |
|---|---|
| §3.1 negator rules table | Task 1 Steps 1, 4 (one test per row + every decline) |
| §3.1 `IsQueryNativeComparison` gate is load-bearing | Task 1 Steps 3, 4, 8 (teeth-check 2 and 3) |
| §3.2 rendered forms | Task 1 Step 1, Task 2 Step 1, Task 4 Step 2 (MQL) |
| §3.3 empty/missing/null correctness | Task 4 Steps 2 (`..._over_an_empty_or_absent_array_is_true`), 5 |
| §4 components 1–4 | Tasks 1, 1, 2, 3 respectively |
| §4 two domain invariants | Task 1 Step 1 (last two tests) |
| §5 inherited guards | Task 2 Steps 1, 7; Task 4 Step 4 |
| §6 flip 1 (owned `All`) | Task 4 |
| §6 flip 2 (EF-335 + spec delta) | Task 3; Task 6 Steps 1–2 |
| §6 flip 3 (incidental `!(cmp)`) | Task 0 Step 4 → Task 3 Step 5 |
| §7 non-goals | Task 4 Step 4 declines; Task 6 Step 4 documents |
| §8 spike items 1–8 | Task 0 Steps 1–5 |
| §9 differential matrix | Task 5 |
| §9 two-seed pattern | Task 4 Step 1 |
| §9 guards proven by deletion | Tasks 1/2/4/5 teeth-check steps |
| §9 sweeps + hygiene + AGENTS.md | Task 6 |

No gaps.

**2. Placeholder scan.** The `<NumericProp>`/`<StringProp>`/`<value>` markers in Task 3 Step 1 and the assertion body in Task 4 Step 4's `Primitive_collection_All_…` are deliberate and each carries an explicit instruction for what to substitute and where the value comes from (that file's existing fixture; the Task 0 spike's recorded finding) — they cannot be pre-filled here without inventing fixture members that may not exist. Every other step contains runnable content.

**3. Type consistency.** `TryNegate(MongoExpression, out MongoExpression?)` is used identically in Tasks 1, 2, 3. `IsQueryNativeComparison(MongoBinaryExpression)` is `internal static` where Task 1 defines it and where Task 1's negator consumes it. `MongoQuantifierKind{Any,All}` is defined and consumed only in Task 2. Row builders (`Post`, `PostWithoutRank`, `Row`, `AllPassRow`, `OneFailsRow`, `MissingFieldRow`, `NullFieldRow`, `EmptyPostsRow`, `MissingPostsRow`, `NullPostsRow`, `PostWithComments`, `Comment`, `Note`, `RowWithNotes`, `Seed`, `SeedMatrix`, `SeedWellFormed`) are defined in Task 4 Step 1 and reused with the same names and arities in Tasks 4 Steps 2–4 and Task 5. `AssertNativeAndParity`/`AssertNativeOnlyMatches`/`AssertDeclinesCleanly` keep the signatures they have in `NativeOwnedCollectionPredicateTests.cs`.
