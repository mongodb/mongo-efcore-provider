# Native owned-collection `$elemMatch` predicates — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Any` quantifiers over owned (embedded) collection navigations translate natively to
index-usable `$elemMatch`, instead of falling back to driver-LINQ.

**Architecture:** One new AST node (`MongoElemMatchExpression`) with three touch points — the query-dialect
renderer (`MongoQueryLanguageRenderer`), the expression translator (`MongoExpressionTranslator`), and the
scope-prefix rewriter (`MongoFieldPrefixRewriter`). The element predicate is translated by a **second,
element-scoped** `MongoExpressionTranslator` so its field paths come out element-relative — exactly what
`$elemMatch` requires. The array path is built from **scope-relative** navigation element names (not
`GetDocumentPath()`), which is what lets it compose with prefix rewriting and makes nested `Any` work.

**Tech Stack:** C# / .NET (net8.0 for EF8+EF9, net10.0 for EF10), EF Core 8/9/10, MongoDB C# driver,
xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-07-27-native-owned-collection-predicates-design.md`
**Branch:** `EF-322-owned-collection-predicates-native`, base `3be3106` (the spec commit), stacked on the
native tip `2a9b56e` = `origin/NativeQueryOngoing`.

## Global Constraints

- All new types are `internal`. The provider's public surface does not change. This slice is **not** a
  breaking change (fallback→native with unchanged results; emitted MQL is explicitly non-contract).
- `src/` is `<Nullable>enable</Nullable>` — annotate accordingly; avoid `!` null-forgiving where a real check
  is cheap.
- Must build and test clean under **all three** configurations: `Debug EF8`, `Debug EF9`, `Debug EF10`.
  No `#if` is expected to be needed; if one becomes necessary, use the `EF8`/`EF9`/`EF10` symbols.
- Preserve file BOMs on existing files. New `.md` files: no BOM. New `.cs` files: match the sibling file in
  the same directory (`MongoDB.EntityFrameworkCore` sources carry the Apache copyright header — copy it
  verbatim from a sibling).
- Every decline path is `return null` / `return false` → the query routes to driver-LINQ. **Never** throw a
  new exception type for an unsupported shape, and never emit a `$match` that could silently match the wrong
  documents.
- Test env: leave **both** `MONGODB_URI` and `ATLAS_URI` unset so each `dotnet test` process gets its own
  isolated `mongodb/mongodb-atlas-local` testcontainer (per-run isolation; parallel agents don't collide).
- Tests run serially within a process (assembly-level parallelization is disabled). Do not add
  parallelization.
- Commit after each task. The whole slice is squashed to **one** commit at the end (Task 6), including
  `docs/superpowers/{specs,plans}` but **excluding** `.superpowers/sdd` (git-ignored working notes).

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs` | **Create.** The AST node: array path, optional element predicate, negation flag. | 2 |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` | **Modify.** One `RenderNode` case + `RenderElemMatch` + the `IsQueryDialectRenderable` classifier. | 2 |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs` | **Modify.** One case: prefix the array path, leave the element predicate alone. | 2 |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` | **Modify.** `TryMatchAnyMethod`, `TryResolveOwnedCollectionPath`, the `TranslateNode` quantifier case, and the `Negated`-flip in the existing `Not` case. | 3 |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs` | **Modify.** Rendered-BSON tests (this is where the emitted MQL is pinned) + classifier tests. | 2 |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs` | **Modify.** The array-path-prefixed / child-untouched test. | 2 |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` | **Modify.** Translator accept/decline matrix; extend the shared `OwnedBlog` model with owned collections. | 3 |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionPredicateTests.cs` | **Create.** End-to-end `NativeOnly` routing proofs, `DriverLinq` parity, and clean-decline assertions. | 4 |
| `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | **Modify.** As-built note. | 5 |

**Where the emitted MQL is pinned:** in the renderer **unit** tests (Task 2), asserted as exact
`BsonDocument.Parse(...)` values. The functional `Native*` tests in this repo do not assert MQL — they assert
`NativeOnly` routing plus `DriverLinq` value parity (see `NativeOwnedSubPropertyTests`,
`NativeOwnedCollectionWholeEntityTests`). Follow that convention; do not invent an MQL-capture harness.

---

### Task 1: Slice-0 de-risking spike (throwaway) — **GATE**

The previous two slices were each plan-invalidated by an EF expression-tree surprise caught exactly here.
**All code written in this task is reverted at the end.** The deliverable is a findings document.

**Files:**
- Create (working notes, git-ignored, NOT committed to the slice): `.superpowers/sdd/EF-322-owned-collection-predicates-spike.md`
- Scratch edits anywhere under `src/` and `tests/` — all reverted in Step 8.

**Interfaces:**
- Consumes: nothing.
- Produces: the findings doc. Tasks 2–4 **must** be adjusted to whatever it records; in particular Task 3's
  `TryMatchAnyMethod` and `UnwrapCollectionSource` are written against the *actual* tree shape, not the
  assumed one.

- [ ] **Step 1: Create the spike scratch branch**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout -b sp-owned-coll-any-spike
git log --oneline -1   # expect 3be3106 (the spec commit)
```

- [ ] **Step 2: Write a temporary dump test that prints the real EF expression tree — the #1 unknown**

Add this to a scratch file `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/SpikeDumpTests.cs`.
It intercepts translation by running under `NativeOnly` and dumping what the translator receives. The
simplest reliable dump point is a temporary `Console.WriteLine` inside
`MongoExpressionTranslator.TranslateNode`'s `default:` branch (add it as a scratch edit), printing
`node.NodeType`, `node.GetType().Name`, and `node.ToString()`:

```csharp
// SCRATCH — remove in Step 8. In MongoExpressionTranslator.TranslateNode, first line of `default:`
System.Console.WriteLine($"[SPIKE] {node.NodeType} / {node.GetType().Name} / {node}");
```

```csharp
/* Copyright 2023-present MongoDB Inc. ... (copy header from a sibling test file) */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class SpikeDumpTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
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
        public string Heading { get; set; } = "";
        public int Rank { get; set; }
        public Geo Geo { get; set; } = null!;
        public List<Comment> Comments { get; set; } = [];
    }

    private class Comment { public string Text { get; set; } = ""; }
    private class Geo { public string Country { get; set; } = ""; }
    private class Home { public List<Note> Notes { get; set; } = []; }
    private class Note { public string Body { get; set; } = ""; }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p =>
        {
            p.OwnsOne(x => x.Geo);
            p.OwnsMany(x => x.Comments);
        });
        mb.Entity<Blog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));
    };

    [Fact]
    public void Dump_owned_collection_Any_shapes()
    {
        var coll = database.MongoDatabase.GetCollection<Blog>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Dump_owned_collection_Any_shapes))
            + Guid.NewGuid().ToString("N")[..8]);

        using var db = SingleEntityDbContext.Create(coll, modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.DriverLinq);
            });

        // Each of these prints its predicate body shape via the scratch WriteLine in TranslateNode.
        Console.WriteLine("--- Any(pred) ---");
        _ = db.Entities.Where(b => b.Posts.Any(p => p.Heading == "x")).ToList();
        Console.WriteLine("--- Any() ---");
        _ = db.Entities.Where(b => b.Posts.Any()).ToList();
        Console.WriteLine("--- !Any(pred) ---");
        _ = db.Entities.Where(b => !b.Posts.Any(p => p.Heading == "x")).ToList();
        Console.WriteLine("--- multi-condition ---");
        _ = db.Entities.Where(b => b.Posts.Any(p => p.Heading == "x" && p.Rank > 2)).ToList();
        Console.WriteLine("--- nested Any ---");
        _ = db.Entities.Where(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t"))).ToList();
        Console.WriteLine("--- through owned ref ---");
        _ = db.Entities.Where(b => b.Home.Notes.Any(n => n.Body == "b")).ToList();
        Console.WriteLine("--- nested owned scalar leaf in element ---");
        _ = db.Entities.Where(b => b.Posts.Any(p => p.Geo.Country == "US")).ToList();
        Console.WriteLine("--- primitive collection ---");
        _ = db.Entities.Where(b => b.Tags.Any(t => t == "x")).ToList();
    }
}
```

- [ ] **Step 3: Run it and record the tree shapes**

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpikeDumpTests" --logger "console;verbosity=detailed"
```

Record, for each of the 8 shapes: the outer node type reaching `TranslateNode`; whether the quantifier is
`Enumerable.Any` or `Queryable.Any`; whether the lambda is quoted; and **whether the source is wrapped**
(e.g. `MaterializeCollectionNavigationExpression`, a `Queryable.Where` chain, an `EntityQueryRootExpression`)
or is a plain member / `EF.Property` chain. This is the fact Task 3 is written against.

- [ ] **Step 4: Confirm the MQL semantics against the real server**

Write a scratch test that runs raw aggregations on a seeded collection (populated / empty array / missing
field / explicit BSON `null` array), and record actual match sets for:

```csharp
// Against docs: {Posts:[{Heading:"x",Rank:1}]}, {Posts:[]}, {} (no Posts), {Posts:null}
BsonDocument.Parse("{ Posts: { $elemMatch: { Heading: 'x' } } }")
BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Heading: 'x' } } } }")
BsonDocument.Parse("{ 'Posts.0': { $exists: true } }")
BsonDocument.Parse("{ 'Posts.0': { $exists: false } }")
BsonDocument.Parse("{ Posts: { $elemMatch: {} } }")                    // is an empty $elemMatch legal?
BsonDocument.Parse("{ Posts: { $elemMatch: { $expr: { $gt: ['$Rank', 0] } } } }")  // is $expr legal here?
```

Then compare each against what LINQ-to-objects `Any` returns for the equivalent in-memory shapes. Record
any divergence. Also compare `Posts.Any(p => p.Heading == "x" && p.Rank > 2)` against the dotted-path
alternative `{ "Posts.Heading": "x", "Posts.Rank": { $gt: 2 } }` on a document whose two conditions match
**different** elements — this empirically documents why approach B was rejected.

- [ ] **Step 5: Confirm the converted-leaf element predicate needs no guard**

Add a value-converted property (e.g. `.HasConversion(...)` on `Post.Rank`, or a non-default
`BsonRepresentation`) to the scratch model and check that `Where(b => b.Posts.Any(p => p.Rank == 2))` under
`DriverLinq` and a hand-run `$elemMatch` agree. This re-confirms finding D1 from the previous slice for an
element-scoped leaf.

- [ ] **Step 6: Check whether `MongoFieldPrefixRewriter` is reachable with an `$elemMatch`**

Construct an owned `SelectMany` whose inner filter contains an `Any`, e.g.:

```csharp
_ = db.Entities.SelectMany(b => b.Posts.Where(p => p.Comments.Any(c => c.Text == "t")), (b, p) => p.Heading).ToList();
```

Record whether this reaches `NativeSelectManyBinder.TryBuildOwnedInnerFilter` (put a temporary
`Console.WriteLine` there) and therefore whether the Task-2 rewriter case is load-bearing today or purely
defensive. Note: `TryBuildOwnedInnerFilter` builds a **single-scope translator on the element type**, and
Task 3's `TryResolveOwnedCollectionPath` has no `IsDocumentRoot` guard, so this path *can* produce an
`$elemMatch` node that reaches the rewriter.

- [ ] **Step 7: Measure the blast radius**

```bash
# Which OwnedEntityTests cases exercise these shapes, and do they still pass?
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~OwnedEntityTests"
```

The spike has no production change yet, so this is a **baseline**. Record the pass count. Then record the
prediction to be checked in Task 5: `:160`/`:173` (`p.locations.Any(l => l == location)` — whole-element
equality) should **not** flip to native; `:1040`, `:1052`, and the nested chains at `:1138`, `:1254`,
`:1257`, `:1260`, `:1287`, `:1308` should.

- [ ] **Step 8: Write the findings doc, then revert everything**

Write `.superpowers/sdd/EF-322-owned-collection-predicates-spike.md` covering all seven spec §8 questions
with a **GO / GO-WITH-CHANGES / NO-GO** verdict, then:

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout -- . && git clean -fd src tests   # discard ALL scratch edits
git checkout EF-322-owned-collection-predicates-native
git branch -D sp-owned-coll-any-spike
git status --short                              # expect clean (the .superpowers/sdd doc is git-ignored)
```

**GATE — do not start Task 2 until:** the actual EF tree shape for each `Any` spelling is recorded; the
`$elemMatch` / `.0`-`$exists` semantics are confirmed to match LINQ `Any` on populated / empty / missing /
`null` arrays; and the verdict is GO. If the tree shape or semantics differ from the spec's assumptions,
**update the spec and this plan first**, then proceed.

---

### Task 2: The `$elemMatch` node, its renderer, and the query-dialect classifier

Independently testable via unit tests over hand-built nodes; nothing produces the node yet (Task 3 wires it).

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (the
  `RenderNode` switch at `:74-88`; add `RenderElemMatch` + `IsQueryDialectRenderable`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs:31-49`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs`

**Interfaces:**
- Consumes: `MongoExpression` (base), `MongoFieldExpression`, `MongoBinaryExpression`,
  `MongoUnaryExpression`, `MongoInExpression`, `MongoRegexExpression`, `MongoConstantExpression`,
  `MongoParameterExpression`, `PlaceholderTable`, `MongoValueRenderer.RenderValue`.
- Produces, for Task 3:
  - `internal sealed class MongoElemMatchExpression : MongoExpression` with constructor
    `MongoElemMatchExpression(string arrayPath, MongoExpression? elementPredicate, bool negated)` and
    properties `string ArrayPath`, `MongoExpression? ElementPredicate`, `bool Negated`, `override Type Type
    => typeof(bool)`.
  - `public static bool MongoQueryLanguageRenderer.IsQueryDialectRenderable(MongoExpression node)`.

- [ ] **Step 1: Write the failing renderer tests**

Add to `MongoQueryLanguageRendererTests.cs`. First add the owned model + helper near the existing
`Customer`/`GetProperty<T>` helpers at the top of the class:

```csharp
    private class Blog
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = null!;
        public int Rank { get; set; }
    }

    // A property of the owned COLLECTION ELEMENT type (Post), for building element-relative field refs.
    private static IProperty GetPostProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(b => b.Posts));
        return db.Model.FindEntityType(typeof(Blog))!
            .FindNavigation(nameof(Blog.Posts))!.TargetEntityType.FindProperty(propertyName)!;
    }
```

`using System.Collections.Generic;` may be needed at the top of the file — add it if not present.

Then the tests:

```csharp
    // ------------------------------------------------------------------
    // $elemMatch over an owned (embedded) array
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_elem_match_with_element_relative_child()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Heading"),   // element-relative, NOT "Posts.Heading"
                new MongoConstantExpression("x", heading)),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $elemMatch: { Heading: 'x' } } }"), rendered);
    }

    [Fact]
    public void Renders_multi_condition_elem_match_as_a_single_element_match()
    {
        // The whole point of $elemMatch over the dotted-path alternative: BOTH conditions must hold for
        // the SAME element. Pinning the rendered shape locks that semantic.
        var heading = GetPostProperty(nameof(Post.Heading));
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.AndAlso,
                new MongoBinaryExpression(
                    MongoBinaryOperator.Equal,
                    new MongoFieldExpression(heading, "Heading"),
                    new MongoConstantExpression("x", heading)),
                new MongoBinaryExpression(
                    MongoBinaryOperator.GreaterThan,
                    new MongoFieldExpression(rank, "Rank"),
                    new MongoConstantExpression(2, rank))),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $elemMatch: { Heading: 'x', Rank: { $gt: 2 } } } }"), rendered);
    }

    [Fact]
    public void Renders_negated_elem_match_with_not()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Heading"),
                new MongoConstantExpression("x", heading)),
            negated: true);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Heading: 'x' } } } }"), rendered);
    }

    [Fact]
    public void Renders_bare_Any_as_array_index_exists()
    {
        // { "Posts.0": { $exists: true } } is index-usable AND correct for both an empty array and a
        // MISSING field ({ Posts: { $ne: [] } } would wrongly match a missing field).
        var pred = new MongoElemMatchExpression("Posts", elementPredicate: null, negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: true } }"), rendered);
    }

    [Fact]
    public void Renders_negated_bare_Any_as_array_index_not_exists()
    {
        var pred = new MongoElemMatchExpression("Posts", elementPredicate: null, negated: true);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: false } }"), rendered);
    }

    [Fact]
    public void Renders_nested_elem_match_with_relative_inner_path()
    {
        // The inner array path is relative to the ELEMENT ("Comments"), not the root ("Posts.Comments").
        var heading = GetPostProperty(nameof(Post.Heading));
        var inner = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Text"),   // property identity is irrelevant to rendering
                new MongoConstantExpression("t", heading)),
            negated: false);
        var pred = new MongoElemMatchExpression("Posts", inner, negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { Comments: { $elemMatch: { Text: 't' } } } } }"),
            rendered);
    }

    // ------------------------------------------------------------------
    // IsQueryDialectRenderable — the classifier the translator gates $elemMatch children on
    // ------------------------------------------------------------------

    [Fact]
    public void IsQueryDialectRenderable_accepts_a_field_to_constant_comparison()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(2, rank));

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_field_to_field_comparison()
    {
        // Field-to-field has no query-dialect form: RenderNode would fall through to RenderAsExpr ($expr),
        // which is not usable inside $elemMatch.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Other"));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_Not_over_a_non_field_operand()
    {
        // RenderUnary supports Not only over a bare MongoFieldExpression and THROWS otherwise.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(2, rank)));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void IsQueryDialectRenderable_recurses_through_elem_match_and_conjunctions()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var good = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(1, rank)),
            negated: false);
        var bad = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Other")),
            negated: false);

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, good, good)));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, good, bad)));
        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoElemMatchExpression("Posts", elementPredicate: null, negated: false)));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
dotnet build tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
```

Expected: **compile errors** — `MongoElemMatchExpression` does not exist, and
`MongoQueryLanguageRenderer.IsQueryDialectRenderable` does not exist. That is the failing state for this step.

- [ ] **Step 3: Create the AST node**

`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs` — copy the Apache header
verbatim from `Expressions/MongoInExpression.cs`, then:

```csharp
using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents an existential quantifier over an embedded (owned) array field: at least one element of
/// <see cref="ArrayPath"/> satisfies <see cref="ElementPredicate"/>, optionally negated. Renders to
/// <c>$elemMatch</c> (or, for the bare <c>Any()</c> form where <see cref="ElementPredicate"/> is
/// <see langword="null"/>, to an array-index <c>$exists</c> test).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArrayPath"/> is relative to the ENCLOSING document scope, and
/// <see cref="ElementPredicate"/>'s field paths are relative to the ARRAY ELEMENT — not to the document
/// root. That is what <c>$elemMatch</c> requires, and it is what makes nesting work: an
/// <see cref="MongoElemMatchExpression"/> inside another one carries an element-relative array path of its
/// own.
/// </para>
/// <para>
/// Consequence for <see cref="NativeTranslation.MongoFieldPrefixRewriter"/>: prefixing must apply to
/// <see cref="ArrayPath"/> only. Rewriting the element predicate would mis-address every field inside the
/// <c>$elemMatch</c>.
/// </para>
/// </remarks>
internal sealed class MongoElemMatchExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoElemMatchExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// The dotted document path of the embedded array, relative to the enclosing scope (e.g. <c>"Posts"</c>,
    /// or <c>"Home.Notes"</c> when reached through an owned single reference).
    /// </param>
    /// <param name="elementPredicate">
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths; or
    /// <see langword="null"/> for a bare <c>Any()</c> (array-is-non-empty) test.
    /// </param>
    /// <param name="negated"><see langword="true"/> for the negated form (<c>!source.Any(...)</c>).</param>
    public MongoElemMatchExpression(string arrayPath, MongoExpression? elementPredicate, bool negated)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Negated = negated;
    }

    /// <summary>The dotted document path of the embedded array, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths, or
    /// <see langword="null"/> for a bare <c>Any()</c> test.
    /// </summary>
    public MongoExpression? ElementPredicate { get; }

    /// <summary><see langword="true"/> for the negated form (<c>!source.Any(...)</c>).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
```

- [ ] **Step 4: Add the renderer case, `RenderElemMatch`, and the classifier**

In `MongoQueryLanguageRenderer.cs`, add the case to the `RenderNode` switch — **before** the
`_ => RenderAsExpr(node, placeholders)` catch-all:

```csharp
            MongoRegexExpression regex => RenderRegex(regex, placeholders),
            MongoElemMatchExpression elemMatch => RenderElemMatch(elemMatch, placeholders),
            _ => RenderAsExpr(node, placeholders)
```

Then add, after `RenderRegex`:

```csharp
    // ------------------------------------------------------------------
    // Existential quantifier over an embedded array ($elemMatch)
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a <see cref="MongoElemMatchExpression"/>.
    /// <para>
    /// With an element predicate: <c>{ path: { $elemMatch: &lt;child&gt; } }</c>, negated as
    /// <c>{ path: { $not: { $elemMatch: &lt;child&gt; } } }</c>. The child goes through the same
    /// <see cref="RenderNode"/> dispatch and its field names stay ELEMENT-RELATIVE — they are deliberately
    /// not prefixed with the array path, which is exactly what <c>$elemMatch</c> expects. Multi-condition
    /// children merge into one document via <see cref="CombineAnd"/>, so all conditions must hold for the
    /// SAME element.
    /// </para>
    /// <para>
    /// Without one (bare <c>Any()</c>): <c>{ "path.0": { $exists: true } }</c> — index-usable, and true for
    /// exactly those documents whose array has at least one element. A missing field and an empty array both
    /// correctly yield false, whereas <c>{ path: { $ne: [] } }</c> would wrongly match a missing field.
    /// Negated: <c>$exists: false</c>.
    /// </para>
    /// <para>
    /// The child is guaranteed to have a query-dialect rendering because
    /// <see cref="IsQueryDialectRenderable"/> gates node construction in
    /// <c>MongoExpressionTranslator</c> — <c>$expr</c> is not usable inside <c>$elemMatch</c>.
    /// </para>
    /// </summary>
    private BsonDocument RenderElemMatch(MongoElemMatchExpression elemMatch, PlaceholderTable placeholders)
    {
        if (elemMatch.ElementPredicate is null)
            return new BsonDocument(
                elemMatch.ArrayPath + ".0", new BsonDocument("$exists", !elemMatch.Negated));

        var body = new BsonDocument(
            "$elemMatch", (BsonDocument)RenderNode(elemMatch.ElementPredicate, placeholders));

        return elemMatch.Negated
            ? new BsonDocument(elemMatch.ArrayPath, new BsonDocument("$not", body))
            : new BsonDocument(elemMatch.ArrayPath, body);
    }

    // ------------------------------------------------------------------
    // Query-dialect renderability — MUST STAY IN SYNC WITH RenderNode ABOVE
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns whether <paramref name="node"/> has a QUERY-dialect rendering: whether
    /// <see cref="RenderNode"/> would render it without falling through to <see cref="RenderAsExpr"/> (which
    /// emits <c>$expr</c>, the aggregation dialect) and without throwing.
    /// </summary>
    /// <remarks>
    /// Used by <c>MongoExpressionTranslator</c> to decline an <c>$elemMatch</c> whose element predicate has
    /// no query-dialect form. This is a <b>correctness</b> gate, not an indexing preference:
    /// <c>$expr</c> inside <c>$elemMatch</c> is a hard server error — <c>Command find failed: $expr can only
    /// be applied to the top-level document</c> — so a child that slipped through to the <c>$expr</c>
    /// catch-all would make the whole query throw at execution time, under <c>Native</c> as well as
    /// <c>NativeOnly</c>. Declining at translate time falls the query back to driver-LINQ instead.
    /// <b>This method and <see cref="RenderNode"/> must be changed together:</b> a node this method admits
    /// but <see cref="RenderNode"/> sends to <c>$expr</c> (or throws on) becomes exactly that runtime
    /// failure.
    /// </remarks>
    public static bool IsQueryDialectRenderable(MongoExpression node)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
                => IsQueryDialectRenderable(a.Left) && IsQueryDialectRenderable(a.Right),
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
                => IsQueryDialectRenderable(o.Left) && IsQueryDialectRenderable(o.Right),
            MongoBinaryExpression comparison => IsQueryNativeComparison(comparison),
            // RenderUnary supports Not over a bare field only, and throws otherwise.
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoFieldExpression => true,
            // RenderInValues throws for any values node other than a constant enumerable or a parameter.
            MongoInExpression inExpr
                => inExpr.Values is MongoConstantExpression { Value: System.Collections.IEnumerable }
                    or MongoParameterExpression,
            // RenderRegex throws for a parameterized term — only a constant is baked into a pattern.
            MongoRegexExpression { Term: MongoConstantExpression { Value: string } } => true,
            MongoElemMatchExpression { ElementPredicate: null } => true,
            MongoElemMatchExpression elemMatch => IsQueryDialectRenderable(elemMatch.ElementPredicate),
            _ => false
        };
```

- [ ] **Step 5: Add the prefix-rewriter case**

In `MongoFieldPrefixRewriter.cs`, add to the `Rewrite` switch, before the `_ => throw`:

```csharp
            // Prefix the ARRAY path only. The element predicate's field paths are ELEMENT-relative (that is
            // what $elemMatch requires), so rewriting them would mis-address every field inside the
            // $elemMatch and silently match nothing.
            MongoElemMatchExpression e => new MongoElemMatchExpression(
                prefix + "." + e.ArrayPath, e.ElementPredicate, e.Negated),
```

- [ ] **Step 6: Write the failing prefix-rewriter test**

Add to `MongoFieldPrefixRewriterTests.cs`:

```csharp
    [Fact]
    public void Prefixes_the_elem_match_array_path_and_leaves_the_element_predicate_alone()
    {
        var child = new MongoBinaryExpression(
            MongoBinaryOperator.Equal, Field("Name"),
            new MongoConstantExpression("x", forSerialization: null));
        var expr = new MongoElemMatchExpression("Posts", child, negated: false);

        var rewritten = (MongoElemMatchExpression)MongoFieldPrefixRewriter.Rewrite(expr, "_lookup_Refs");

        Assert.Equal("_lookup_Refs.Posts", rewritten.ArrayPath);
        // The child is element-relative and must be untouched — NOT "_lookup_Refs.Name".
        var childField = (MongoFieldExpression)((MongoBinaryExpression)rewritten.ElementPredicate!).Left;
        Assert.Equal("Name", childField.ElementName);
        Assert.False(rewritten.Negated);
    }
```

- [ ] **Step 7: Run the unit tests and verify they pass**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoQueryLanguageRendererTests|FullyQualifiedName~MongoFieldPrefixRewriterTests"
```

Expected: PASS, all of them. If `Renders_multi_condition_elem_match_as_a_single_element_match` produces
`{ $and: [...] }` instead of a merged document, that is `CombineAnd`'s documented fallback — update the
expected BSON to whatever `CombineAnd` actually produces for two distinct fields **and** verify by hand that
the resulting filter is still semantically "one element satisfies both".

- [ ] **Step 8: Verify all three EF configurations build**

```bash
for cfg in "Debug EF8" "Debug EF9" "Debug EF10"; do
  dotnet build MongoDB.EFCoreProvider.sln -c "$cfg" 2>&1 | tail -3
done
```

Expected: `Build succeeded` for each.

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs
git commit -m "EF-322: add MongoElemMatchExpression + query-dialect renderer and classifier"
```

---

### Task 3: Translator wiring — path resolver, `Any` matcher, and negation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
  (the `Not` case at `:244-262`; a new `MethodCallExpression` case after the regex case at `:280-294`; new
  private helpers near `TryResolveOwnedFieldPath` at `:572-635` and `TryGetMemberOrEFProperty` at `:639-660`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Consumes from Task 2: `MongoElemMatchExpression(string, MongoExpression?, bool)` with `ArrayPath` /
  `ElementPredicate` / `Negated`; `MongoQueryLanguageRenderer.IsQueryDialectRenderable(MongoExpression)`.
- Consumes existing: `MongoExpressionTranslator(IEntityType)` (the single-scope constructor at `:57`),
  `TryGetMemberOrEFProperty` (`:639-660`), `Unwrap` (`:206-209`), `TryTranslate` (`:82`),
  `navigation.IsEmbedded()` and `TargetEntityType.GetContainingElementName()` (from
  `MongoDB.EntityFrameworkCore.Extensions`, already imported).
- Produces: nothing new for later tasks — after this task the feature is functionally complete and Task 4
  proves it end-to-end.

**Tree shape — settled by the Task 1 spike, code below already matches it.** EF hands the translator:

```
Queryable.Any(Call(AsQueryable, [<EF.Property / Member hop chain>]), Quote(lambda))   // Any(pred)
Queryable.Any(Call(AsQueryable, [<EF.Property / Member hop chain>]))                  // Any()  — 1-arg
```

Always the `Queryable` overload, lambda always `Quote`-wrapped, source always wrapped in exactly one
`AsQueryable()` call. No `MaterializeCollectionNavigationExpression`, no `Queryable.Where` pair. Hop chains
reuse the existing `TryGetMemberOrEFProperty` walker unchanged; nested `Any` and the through-owned-ref form
compose for free. The `Enumerable` overload is accepted as well, so hand-built trees behave identically — note
that a C# lambda like `b.Posts.Any(p => ...)` compiles to `Enumerable.Any`, so the unit tests below exercise
that overload and **one** test (Step 2's last) pins the real production shape.

**Atomicity requirement (spike finding).** Task 2's `MongoFieldPrefixRewriter` case is load-bearing, not
defensive: once this task's translator emits `$elemMatch`, an owned `SelectMany` with an inner `Any` filter
reaches the rewriter's existing `Rewrite(...)` call. Confirm Task 2 landed that case before committing this
task — without it, a currently-clean decline becomes a crash thrown from inside pre-existing code.

- [ ] **Step 1: Extend the shared unit-test model with owned collections**

In `MongoExpressionTranslatorTests.cs`, extend the existing `OwnedBlog` model (`:82-106`). These are
additive — existing assertions are unaffected:

```csharp
    private class OwnedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public OwnedAddress Address { get; set; } = null!;
        public List<OwnedPost> Posts { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    private class OwnedAddress
    {
        public string City { get; set; } = "";
        public bool IsPrimary { get; set; }
        public OwnedGeo Geo { get; set; } = null!;
        public List<OwnedNote> Notes { get; set; } = [];
    }

    private class OwnedGeo
    {
        public string Country { get; set; } = "";
    }

    private class OwnedPost
    {
        public string Heading { get; set; } = "";
        public int Rank { get; set; }
        public int Other { get; set; }
        public OwnedGeo Geo { get; set; } = null!;
        public List<OwnedComment> Comments { get; set; } = [];
    }

    private class OwnedComment
    {
        public string Text { get; set; } = "";
    }

    private class OwnedNote
    {
        public string Body { get; set; } = "";
    }

    private static IEntityType GetOwnedBlogEntityType()
    {
        using var db = SingleEntityDbContext.Create<OwnedBlog>(mb =>
        {
            mb.Entity<OwnedBlog>().OwnsOne(b => b.Address, a =>
            {
                a.OwnsOne(x => x.Geo);
                a.OwnsMany(x => x.Notes);
            });
            mb.Entity<OwnedBlog>().OwnsMany(b => b.Posts, p =>
            {
                p.OwnsOne(x => x.Geo);
                p.OwnsMany(x => x.Comments);
            });
        });
        return db.Model.FindEntityType(typeof(OwnedBlog))!;
    }
```

Add `using System.Collections.Generic;` to the file if it is not already present.

- [ ] **Step 2: Write the failing translator tests**

Append to the owned section of `MongoExpressionTranslatorTests.cs`:

```csharp
    // ------------------------------------------------------------------
    // Owned-collection quantifiers → $elemMatch (EF-322)
    // ------------------------------------------------------------------

    [Fact]
    public void Owned_collection_Any_with_predicate_translates_to_elem_match()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Heading == "x"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        Assert.False(elemMatch.Negated);
        var comparison = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        // ELEMENT-RELATIVE: "Heading", NOT "Posts.Heading".
        Assert.Equal("Heading", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_bare_Any_translates_to_elem_match_with_no_element_predicate()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any());

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        Assert.Null(elemMatch.ElementPredicate);
        Assert.False(elemMatch.Negated);
    }

    [Fact]
    public void Negated_owned_collection_Any_flips_the_negated_flag()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => !b.Posts.Any(p => p.Heading == "x"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.True(elemMatch.Negated);
        Assert.NotNull(elemMatch.ElementPredicate);
    }

    [Fact]
    public void Nested_owned_collection_Any_translates_to_nested_elem_match_with_relative_paths()
    {
        // The inner array path must be ELEMENT-relative ("Comments"), not root-relative
        // ("Posts.Comments"). This is the proof that scope-relative path building works.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t")));

        Assert.True(translator.TryTranslate(body, out var result));
        var outer = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", outer.ArrayPath);
        var inner = Assert.IsType<MongoElemMatchExpression>(outer.ElementPredicate);
        Assert.Equal("Comments", inner.ArrayPath);
        var comparison = Assert.IsType<MongoBinaryExpression>(inner.ElementPredicate);
        Assert.Equal("Text", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_Any_through_an_owned_reference_hop_builds_a_dotted_array_path()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Address.Notes.Any(n => n.Body == "b"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Address.Notes", elemMatch.ArrayPath);
    }

    [Fact]
    public void Owned_collection_Any_in_the_exact_shape_EF_produces_resolves()
    {
        // EF hands the translator the Queryable overload, the source wrapped in ONE AsQueryable() call, the
        // lambda Quote-wrapped, and owned-nav hops rewritten to EF.Property calls:
        //   Queryable.Any(Call(AsQueryable, [EF.Property(b, "Posts")]), Quote(p => p.Heading == "x"))
        // A C# lambda compiles to the Enumerable overload instead, so this hand-built tree is the ONLY unit
        // coverage of the shape production queries actually take.
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(OwnedBlog), "b");
        var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(List<OwnedPost>));
        var postsCall = Expression.Call(efProperty, param, Expression.Constant("Posts"));
        var asQueryable = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.AsQueryable) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(OwnedPost));
        var source = Expression.Call(asQueryable, postsCall);
        Expression<Func<OwnedPost, bool>> elementPredicate = p => p.Heading == "x";
        var anyMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(OwnedPost));
        var body = Expression.Call(anyMethod, source, Expression.Quote(elementPredicate));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        var comparison = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Heading", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_Any_with_field_to_field_element_predicate_is_declined()
    {
        // Field-to-field has no query-dialect form and $expr is not usable inside $elemMatch, so the
        // whole quantifier declines (query falls back to driver-LINQ).
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Rank > p.Other));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Owned_collection_Any_with_nested_owned_scalar_leaf_is_declined()
    {
        // p.Geo.Country is a scalar leaf reached through an owned reference INSIDE the element. The
        // element-scoped child translator is not a document root, so TryResolveOwnedFieldPath's
        // IsDocumentRoot guard declines it — a clean decline, not a mis-addressed path.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Geo.Country == "US"));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Primitive_collection_Any_is_declined_by_the_quantifier_matcher()
    {
        // Tags is a primitive collection PROPERTY, not a navigation — FindNavigation returns null, so the
        // path resolver declines. Defensive lock only: in a real query EF's own
        // AllAnyToContainsRewritingExpressionVisitor rewrites `Any(t => t == "x")` into `Contains("x")`
        // BEFORE the native translator sees it, so no Any node reaches this matcher for this shape.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Tags.Any(t => t == "x"));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Whole_element_equality_Any_is_declined()
    {
        // The element parameter itself has no member access to resolve.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var target = new OwnedComment { Text = "t" };
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c == target)));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Two_scope_owned_collection_Any_is_declined()
    {
        // A two-scope (SelectMany-unwind) translator must not engage the owned-collection walk.
        var outerType = GetOwnedBlogEntityType();
        var innerType = GetEntityType<Customer>();
        var outerParam = Expression.Parameter(typeof(OwnedBlog), "o");
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_X");
        Expression<Func<OwnedPost, bool>> elementPredicate = p => p.Heading == "x";
        var anyMethod = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(OwnedPost));
        var body = Expression.Call(
            anyMethod, Expression.Property(outerParam, nameof(OwnedBlog.Posts)), elementPredicate);

        Assert.False(translator.TryTranslate(body, out _));
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```

Expected: the ten new tests **fail** — the accept tests because `TryTranslate` returns `false` (no
quantifier case yet), the decline tests **pass** vacuously (already declining). That vacuous pass is fine and
expected; they become meaningful regression locks once the accept path exists.

- [ ] **Step 4: Add the `Any` matcher and the owned-collection path resolver**

In `MongoExpressionTranslator.cs`, add after `TryGetMemberOrEFProperty` (`:639-660`):

```csharp
    /// <summary>
    /// Matches an existential quantifier call — <c>source.Any()</c> or <c>source.Any(element =&gt; predicate)</c>
    /// — returning the quantifier's SOURCE with its <c>AsQueryable()</c> wrapper stripped and, for the
    /// predicate form, the unquoted element lambda.
    /// </summary>
    /// <remarks>
    /// EF hands the native translator the <see cref="Queryable"/> spelling, with the lambda
    /// <c>Quote</c>-wrapped and the source wrapped in exactly one <c>AsQueryable()</c> call:
    /// <c>Queryable.Any(Call(AsQueryable, [EF.Property(b, "Posts")]), Quote(p =&gt; ...))</c> — confirmed for
    /// every spelling, including the bare 1-argument form, a nested quantifier (whose own source has the
    /// identical shape, rooted on the element parameter), and a collection reached through owned references.
    /// The <see cref="Enumerable"/> spelling is accepted too, so a hand-built expression tree translates
    /// identically to an EF-produced one.
    /// </remarks>
    private static bool TryMatchAnyMethod(
        MethodCallExpression call,
        [NotNullWhen(true)] out Expression? source,
        out LambdaExpression? elementLambda)
    {
        source = null;
        elementLambda = null;

        if (call.Method.Name != nameof(Enumerable.Any))
            return false;

        var declaringType = call.Method.DeclaringType;
        if (declaringType != typeof(Enumerable) && declaringType != typeof(Queryable))
            return false;

        switch (call.Arguments.Count)
        {
            case 1:
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

    // EF wraps a quantifier's collection source in a single Queryable.AsQueryable() call; strip that one
    // layer so the hop walk sees the bare member / EF.Property chain underneath.
    private static Expression UnwrapAsQueryable(Expression source)
    {
        if (source is MethodCallExpression { Arguments: [var inner] } call
            && call.Method.Name == nameof(Queryable.AsQueryable)
            && call.Method.DeclaringType == typeof(Queryable))
        {
            return inner;
        }

        return source;
    }

    /// <summary>
    /// Resolves the SOURCE of an owned-collection quantifier (<c>b.Posts</c>, <c>b.Address.Notes</c>) to the
    /// dotted document path of the embedded array — <b>relative to this translator's scope entity type</b> —
    /// and yields the array's element entity type. Every non-final hop must be an embedded single-reference
    /// navigation; the final hop must be an embedded collection navigation; the chain must be rooted at the
    /// query parameter. Returns <see langword="false"/> (caller falls back to driver-LINQ) for anything else,
    /// including a reference (non-embedded) navigation and a primitive collection property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why scope-relative, and why there is deliberately no <c>IsDocumentRoot</c> guard here</b> (unlike
    /// <see cref="TryResolveOwnedFieldPath"/>): that method builds paths with
    /// <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>, which is always relative to the TRUE document
    /// root, so a translator built on a non-root scope whose caller separately prefixes the result would
    /// double-prefix and silently match nothing — hence its blanket decline. This method instead joins the
    /// hop navigations' own containing element names, so the path is relative to <c>_entityType</c> by
    /// construction. That composes correctly with
    /// <see cref="MongoFieldPrefixRewriter"/> prepending rather than fighting it, and it is what makes a
    /// nested <c>Any</c>-within-<c>Any</c> correct: the element-scoped child translator resolves the inner
    /// array relative to the element, which is exactly what the enclosing <c>$elemMatch</c> expects.
    /// </para>
    /// <para>
    /// Two-scope mode is still declined: a cross-scope quantifier is out of scope for this slice.
    /// </para>
    /// </remarks>
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
        for (var i = 0; i < names.Count; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded())
                return false; // a primitive collection property or a reference nav has no embedded path

            // A collection is allowed only as the FINAL hop (it is the quantifier's source); every
            // intermediate hop must be an owned single reference.
            if (navigation.IsCollection != (i == names.Count - 1))
                return false;

            // The navigation's containing element name is the same source the shapers and pipeline use, so
            // the emitted path matches stored layout (including HasElementName overrides and shared types).
            var elementName = navigation.TargetEntityType.GetContainingElementName();
            if (string.IsNullOrEmpty(elementName))
                return false;

            segments.Add(elementName);
            scopeType = navigation.TargetEntityType;
        }

        arrayPath = string.Join(".", segments);
        elementType = scopeType;
        return true;
    }
```

- [ ] **Step 5: Add the `TranslateNode` quantifier case**

In `MongoExpressionTranslator.TranslateNode`, insert **after** the regex case (`:280-294`) and **before**
`// --- Bare boolean member access (c.Active) ---`:

```csharp
            // --- Existential quantifier over an owned (embedded) collection: source.Any() / source.Any(pred) ---

            case MethodCallExpression call when TryMatchAnyMethod(call, out var quantifierSource, out var elementLambda):
            {
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType))
                    return null; // not an owned-collection source rooted at the query parameter

                if (elementLambda is null)
                    return new MongoElemMatchExpression(arrayPath, elementPredicate: null, negated: false);

                // Translate the element predicate with an ELEMENT-SCOPED translator: its field paths come out
                // element-relative, which is what $elemMatch requires. This is the mirror image of
                // NativeSelectManyBinder.TryBuildOwnedInnerFilter, which translates the same way and then
                // PREFIXES the result with the unwind path.
                var elementTranslator = new MongoExpressionTranslator(elementType);
                if (!elementTranslator.TryTranslate(elementLambda.Body, out var elementPredicate))
                    return null;

                // $expr is not usable inside $elemMatch, and RenderNode's catch-all would silently wrap a
                // non-query-dialect child in $expr. Decline here (translate time) so the query falls back to
                // driver-LINQ instead.
                if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(elementPredicate))
                    return null;

                return new MongoElemMatchExpression(arrayPath, elementPredicate, negated: false);
            }
```

- [ ] **Step 6: Add the negation flip to the existing `Not` case**

In the `case UnaryExpression { NodeType: ExpressionType.Not } not:` block, after the existing
`MongoRegexExpression` flip and before the `MongoFieldExpression` nullable-bool guard:

```csharp
                // !collection.Any(...) → flip Negated rather than wrapping in a generic Not node: RenderUnary
                // supports Not over a bare field only, and $elemMatch has direct query-dialect negations
                // ({ path: { $not: { $elemMatch: ... } } }, and $exists: false for the bare Any() form).
                if (operand is MongoElemMatchExpression elemMatchExpr)
                    return new MongoElemMatchExpression(
                        elemMatchExpr.ArrayPath, elemMatchExpr.ElementPredicate, negated: !elemMatchExpr.Negated);
```

- [ ] **Step 7: Run the unit tests and verify they pass**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```

Expected: PASS, all of them (including the pre-existing owned single-reference tests — the model change in
Step 1 must not have disturbed them).

- [ ] **Step 8: Run the whole unit-test project, and build all three configurations**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
for cfg in "Debug EF8" "Debug EF9" "Debug EF10"; do
  dotnet build MongoDB.EFCoreProvider.sln -c "$cfg" 2>&1 | tail -3
done
```

Expected: unit tests green; `Build succeeded` for all three.

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs
git commit -m "EF-322: translate owned-collection Any quantifiers to \$elemMatch"
```

---

### Task 4: Functional parity, routing, and decline tests

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionPredicateTests.cs`

**Interfaces:**
- Consumes: the complete feature from Tasks 2–3. Test infrastructure: `TemporaryDatabaseFixture`,
  `SingleEntityDbContext.Create`, `MongoDbContextOptionsBuilder.UseQueryMode`,
  `TemporaryDatabaseFixtureBase.CreateCollectionName`, `MongoQueryMode.{Native,NativeOnly,DriverLinq}`,
  `NativeTranslationNotSupportedException`.
- Produces: nothing for later tasks.

**Convention:** `NativeOnly` = routing proof (the query throws if it would fall back); `DriverLinq` = the
value oracle. Copy the structure of `NativeOwnedCollectionWholeEntityTests.cs` exactly — the same
`CreateContext`, `UniqueCollectionName`, seeding-via-`BsonDocument`, and `AssertNativeAndParity` shape.

- [ ] **Step 1: Write the failing test file**

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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322: an Any quantifier over an OWNED (embedded) collection navigation translates natively to
/// $elemMatch. Each admitted shape asserts a NativeOnly routing proof plus Native == DriverLinq value
/// parity; each excluded shape asserts a clean decline (throws only under NativeOnly, correct results
/// under Native).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionPredicateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
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
        public string Heading { get; set; } = "";
        public int Rank { get; set; }
        public int Other { get; set; }
        public Geo Geo { get; set; } = null!;
        public List<Comment> Comments { get; set; } = [];
    }

    private class Comment
    {
        public string Text { get; set; } = "";
    }

    private class Geo
    {
        public string Country { get; set; } = "";
    }

    private class Home
    {
        public List<Note> Notes { get; set; } = [];
    }

    private class Note
    {
        public string Body { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p =>
        {
            p.OwnsOne(x => x.Geo);
            p.OwnsMany(x => x.Comments);
        });
        mb.Entity<Blog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));
    };

    // Seeds five blogs covering every array state that changes $elemMatch / $exists semantics:
    //   "match"    - Posts with a matching element (plus a second, non-matching element)
    //   "nomatch"  - Posts present, no element matches
    //   "empty"    - Posts present but an EMPTY array
    //   "missing"  - no Posts element at all
    //   "null"     - Posts explicitly BSON null
    private IMongoCollection<Blog> SeedBlogs(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "match" },
                { "Home", new BsonDocument { { "Notes", new BsonArray { new BsonDocument { { "Body", "b" } } } } } },
                { "Tags", new BsonArray { "x" } },
                { "Posts", new BsonArray
                    {
                        new BsonDocument
                        {
                            { "Heading", "x" }, { "Rank", 5 }, { "Other", 1 },
                            { "Geo", new BsonDocument { { "Country", "US" } } },
                            { "Comments", new BsonArray { new BsonDocument { { "Text", "t" } } } }
                        },
                        new BsonDocument
                        {
                            { "Heading", "z" }, { "Rank", 1 }, { "Other", 9 },
                            { "Geo", new BsonDocument { { "Country", "FR" } } },
                            { "Comments", new BsonArray() }
                        }
                    }
                }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "nomatch" },
                { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
                { "Tags", new BsonArray { "y" } },
                { "Posts", new BsonArray
                    {
                        new BsonDocument
                        {
                            { "Heading", "y" }, { "Rank", 2 }, { "Other", 2 },
                            { "Geo", new BsonDocument { { "Country", "FR" } } },
                            { "Comments", new BsonArray() }
                        }
                    }
                }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "empty" },
                { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
                { "Tags", new BsonArray() },
                { "Posts", new BsonArray() }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "missing" } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "null" }, { "Posts", BsonNull.Value }
            },
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Runs the query under NativeOnly (routing proof) and under DriverLinq (value oracle), asserts the two
    // agree on the matched set, and returns the matched titles.
    private List<string> AssertNativeAndParity(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        List<string> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, nativeOnly);
        return nativeOnly;
    }

    // Asserts a shape is NOT native: it throws NativeTranslationNotSupportedException under NativeOnly
    // (a clean decline, not a crash) while still returning the DriverLinq answer under Native.
    private void AssertDeclinesCleanly(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => query(db.Entities.AsNoTracking()).ToList());
        }

        List<string> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, native);
    }

    [Fact]
    public void Owned_collection_Any_with_predicate_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_with_predicate_goes_native));

        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "x")));

        Assert.Equal(["match"], titles);
    }

    [Fact]
    public void Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all));

        // "match" has an element with Heading "x" AND Rank 5, so it matches. Crucially, the conditions
        // Heading == "z" && Rank == 5 are each satisfied by DIFFERENT elements of "match" and must NOT match
        // — this is the semantic a dotted-path translation would get wrong.
        var both = AssertNativeAndParity(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "x" && p.Rank == 5)));
        Assert.Equal(["match"], both);

        var split = AssertNativeAndParity(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "z" && p.Rank == 5)));
        Assert.Empty(split);
    }

    [Fact]
    public void Owned_collection_bare_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_bare_Any_goes_native));

        // Present-and-non-empty only: an empty array, a missing field, and an explicit null all yield false.
        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Any()));

        Assert.Equal(["match", "nomatch"], titles);
    }

    [Fact]
    public void Negated_owned_collection_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Negated_owned_collection_Any_goes_native));

        var negatedPredicate = AssertNativeAndParity(
            collection, q => q.Where(b => !b.Posts.Any(p => p.Heading == "x")));
        Assert.Equal(["empty", "missing", "nomatch", "null"], negatedPredicate);

        var negatedBare = AssertNativeAndParity(collection, q => q.Where(b => !b.Posts.Any()));
        Assert.Equal(["empty", "missing", "null"], negatedBare);
    }

    [Fact]
    public void Nested_owned_collection_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Nested_owned_collection_Any_goes_native));

        var titles = AssertNativeAndParity(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t"))));

        Assert.Equal(["match"], titles);
    }

    [Fact]
    public void Owned_collection_Any_through_owned_reference_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_through_owned_reference_goes_native));

        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Home.Notes.Any(n => n.Body == "b")));

        Assert.Equal(["match"], titles);
    }

    [Fact]
    public void Owned_collection_Any_composes_with_other_conjuncts_natively()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_composes_with_other_conjuncts_natively));

        var titles = AssertNativeAndParity(
            collection, q => q.Where(b => b.Title == "match" && b.Posts.Any(p => p.Rank > 3)));

        Assert.Equal(["match"], titles);
    }

    [Fact]
    public void Owned_collection_Any_is_correct_when_tracked()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_is_correct_when_tracked));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        var blogs = db.Entities.Where(b => b.Posts.Any(p => p.Heading == "x")).ToList();

        Assert.Equal(["match"], blogs.Select(b => b.Title));
        Assert.Equal(2, blogs[0].Posts.Count);
    }

    [Fact]
    public void Field_to_field_element_predicate_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(Field_to_field_element_predicate_declines_cleanly));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.Any(p => p.Rank > p.Other)));
    }

    [Fact]
    public void Nested_owned_scalar_leaf_in_element_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(Nested_owned_scalar_leaf_in_element_declines_cleanly));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.Any(p => p.Geo.Country == "US")));
    }

    [Fact]
    public void Primitive_collection_Any_is_unaffected_by_this_slice()
    {
        // EF's AllAnyToContainsRewritingExpressionVisitor rewrites `Tags.Any(t => t == "x")` into
        // `Tags.Contains("x")` before the native translator sees it, so this shape never reaches the new
        // quantifier matcher — it is handled by the pre-existing Contains/$in path, unchanged by this slice.
        //
        // IMPLEMENTER: determine empirically whether that path routes native or falls back, and assert what
        // is actually true (a NativeOnly success assertion OR a NativeOnly
        // Assert.Throws<NativeTranslationNotSupportedException>), plus Native == DriverLinq parity either
        // way. Record the observed routing in your report. Do NOT assume it declines.
        var collection = SeedBlogs(nameof(Primitive_collection_Any_is_unaffected_by_this_slice));

        List<string> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = db.Entities.AsNoTracking().Where(b => b.Tags.Any(t => t == "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().Where(b => b.Tags.Any(t => t == "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, native);
        Assert.Equal(["match"], native);
        // + the routing assertion determined above.
    }

    [Fact]
    public void Owned_SelectMany_with_an_inner_Any_filter_now_works()
    {
        // EMERGENT NEW CAPABILITY (spike-confirmed). Before this slice this shape hard-fails in EVERY mode
        // (including DriverLinq) with InvalidOperationException "could not be translated": the owned
        // SelectMany binder's inner-filter translator could not handle Any, so it declined after the binder
        // had already engaged, and there is no driver-LINQ oracle. It works once $elemMatch exists —
        // NativeSelectManyBinder.TryBuildOwnedInnerFilter's element-scoped translator resolves "Comments"
        // relative to Post, and its existing MongoFieldPrefixRewriter.Rewrite(..., "Posts") call composes
        // that into "Posts.Comments", which correctly addresses the $unwind-ed element.
        //
        // No oracle exists, so the expected value is hand-computed from the seed data: only the "match" blog
        // has a Post whose Comments contain Text "t", and that Post's Heading is "x".
        var collection = SeedBlogs(nameof(Owned_SelectMany_with_an_inner_Any_filter_now_works));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var headings = db.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => p.Comments.Any(c => c.Text == "t")), (b, p) => p.Heading)
            .ToList();

        Assert.Equal(["x"], headings);
    }

    [Fact]
    public void All_over_owned_collection_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(All_over_owned_collection_declines_cleanly));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Heading == "x")));
    }

    [Fact]
    public void Owned_collection_Count_predicate_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Count_predicate_declines_cleanly));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.Count > 1));
    }
}
```

- [ ] **Step 2: Run the new tests**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
unset MONGODB_URI ATLAS_URI
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionPredicateTests"
```

Expected: all PASS.

**If a decline test fails because the shape went native**, that is a scope escape — stop and investigate
before adjusting the assertion; it means the matcher or path resolver admits more than the spec allows.
**If a decline test fails with a different exception type** (not `NativeTranslationNotSupportedException`),
record the actual type: a loud crash instead of a clean decline is a finding, not something to paper over
with `Assert.ThrowsAny`. If the shape hard-fails in *every* mode (no driver-LINQ oracle), follow the
precedent set by `NativeSelectManyTests.Filtered_owned_nested_subproperty_predicate_hard_fails_in_every_mode_not_double_prefixed`:
assert `ThrowsAny` in all three modes plus `IsNotType<KeyNotFoundException>` to pin "clean decline, not
wrong data".

- [ ] **Step 3: Run the neighbouring suites for regressions**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~OwnedEntityTests|FullyQualifiedName~NativeOwnedCollectionWholeEntityTests|FullyQualifiedName~NativeOwnedSubPropertyTests|FullyQualifiedName~NativeSelectManyTests|FullyQualifiedName~NativeGateRoutingTests"
```

Expected: all PASS. `OwnedEntityTests` is the pre-existing behavioral oracle — several of its cases now
silently route native; they must still pass unchanged.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionPredicateTests.cs
git commit -m "EF-322: functional coverage for owned-collection \$elemMatch predicates"
```

---

### Task 5: Validate the eligibility delta and write the as-built docs

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: the finished feature and its tests.
- Produces: a validated eligibility delta and the as-built note the next slice reads first.

- [ ] **Step 1: Confirm the flip set matches the spike's prediction**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
grep -rn "still_falls_back\|falls_back\|Fallback" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ \
  | grep -i "any\|owned" | head -20
```

Expected: no test asserts fallback for an owned-collection `Any`, so **no assertion inversions**. If one
turns up, update it to assert native **and** verify the data it returns is correct against `DriverLinq`
before changing the assertion.

- [ ] **Step 2: Run the `NativeOnly` EF10 spec sweep and compare against the baseline**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
unset MONGODB_URI ATLAS_URI
RESULTS=$(mktemp -d)
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory "$RESULTS"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory "$RESULTS"
echo "results in $RESULTS"
```

Expected: `Native` 4589 passed / 0 failed / 19 skipped, and `NativeOnly` 2192 passed / 2397 failed / 19
skipped — i.e. **exactly the recorded baseline, zero delta**, because Northwind has no owned collections.
A non-zero delta must be explained before proceeding (note: an atlas-local container run gives the
4608-total numbers above; a plain replica-set `mongod` with `ATLAS_URI=Disabled` gives 2190/2285/19 out of
4494 because ~112 Atlas vector-search tests do not run — the invariant that matters is the **delta**, not the
absolute number).

- [ ] **Step 3: Write the as-built note in `Query/AGENTS.md`**

Add a note in the same style and position as the existing owned-slice notes (the owned single-reference
sub-property note is at `:71`). It must state:

- **What went native:** `Any()` / `Any(pred)` over an owned (embedded) collection navigation, negated forms,
  nested `Any`-within-`Any`, and collections reached through owned single-reference hops → `$elemMatch`
  (`{"path.0": {$exists: …}}` for the bare `Any()` form).
- **The mechanism:** `MongoElemMatchExpression` + `MongoExpressionTranslator.TryMatchAnyMethod` /
  `TryResolveOwnedCollectionPath` + `MongoQueryLanguageRenderer.RenderElemMatch`.
- **The scope-relative path decision and why it differs from `TryResolveOwnedFieldPath`** — the latter uses
  `GetDocumentPath()` (absolute) and therefore carries a blanket `IsDocumentRoot` decline; the former joins
  navigation containing-element names relative to the scope, which composes with `MongoFieldPrefixRewriter`
  and is what makes nested `Any` correct. Note that relativizing the scalar resolver the same way is an open
  follow-on that would supersede that blanket decline.
- **`MongoFieldPrefixRewriter` invariant:** prefix `ArrayPath` only; the element predicate is
  element-relative and must never be rewritten.
- **`IsQueryDialectRenderable` must stay in sync with `RenderNode`** — and *why* the child is restricted to
  query dialect (`$expr` is not usable inside `$elemMatch`; index-first).
- **Deferred / still falls back:** `All`, `.Count` in a predicate, embedded-collection projections
  (`Select(b => b.Posts.Count)`, array projections), primitive-element collections, whole-element equality,
  `Contains`, non-query-dialect element predicates, two-scope (cross-scope `SelectMany`) quantifiers, and a
  nested owned *scalar* leaf inside an element.
- **Flips:** zero spec-suite delta; several `OwnedEntityTests` cases now route native silently and remain the
  behavioral oracle.

Update the two `Deferred` bullets in the existing owned single-reference note that name
`Where(e => e.Posts.Any(p => p.Title == ...))` as deferred, so they no longer contradict this note. Leave the
owned-collection *projection* deferral in place — it is still deferred.

- [ ] **Step 4: Update the epic status report**

In `docs/native-query-status-EF-322.md`, add owned-collection `Any` → `$elemMatch` to §3 ("What's native
today") and remove/adjust the corresponding line in §4 / §5. Keep the measured test counts as they are and
note they are point-in-time from tip `1dd7862`.

- [ ] **Step 5: Run the full three-version test suite**

Invoke the `/test-all` skill (foreground; it runs EF8/EF9/EF10 in parallel with per-version isolated
testcontainers). Expected: **zero failures** in all three, with pass counts at or above the previous slice's
(EF8 7424 / EF9 7785 / EF10 7382) plus the new tests.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md docs/native-query-status-EF-322.md
git commit -m "EF-322: as-built notes for owned-collection \$elemMatch predicates"
```

---

### Task 6: Final whole-branch review, squash, and finishing

**Files:** none modified except as review fixes require.

**Interfaces:**
- Consumes: the complete slice, `3be3106..HEAD`.
- Produces: one squashed slice commit, ready to fast-forward onto `origin/NativeQueryOngoing`.

- [ ] **Step 1: Create the safety branch**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git branch EF-322-owned-collection-predicates-native-presquash
git log --oneline 2a9b56e..HEAD
```

- [ ] **Step 2: Run the final whole-branch review**

Invoke `/review-ef-core-provider` for the branch diff (base `2a9b56e`). Additionally dispatch an explicit
**silent-wrong-data hunt** over the diff, focused on:

- Can `TryResolveOwnedCollectionPath` ever produce a path that is wrong for the scope it is used in
  (root vs. element vs. `SelectMany`-prefixed)? The absent `IsDocumentRoot` guard is the thing a reviewer
  who knows the C1 regression will flag first — it must be justified by the scope-relative construction, not
  by assumption.
- Can `IsQueryDialectRenderable` admit anything `RenderNode` would send to `$expr` or throw on?
- Can `MongoFieldPrefixRewriter` reach an `$elemMatch` and mis-prefix the child?
- Does the multi-condition `$elemMatch` really require one element to satisfy all conditions
  (`CombineAnd` merging), including when the conditions are on the same field?
- Does negation stay correct for empty / missing / explicit-null arrays?

Fix anything Critical or Important, re-review, and record the verdict.

- [ ] **Step 3: Re-verify after any review fix**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionPredicateTests|FullyQualifiedName~OwnedEntityTests"
```

If any production file changed in Step 2, re-run the full `/test-all` as well.

- [ ] **Step 4: Squash to one slice commit**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git reset --soft 2a9b56e
git status --short   # expect: spec + plan + src + tests + AGENTS.md + status doc, and NOTHING from .superpowers/sdd
git commit -m "EF-322: owned-collection Any predicates go native (\$elemMatch)"
git diff EF-322-owned-collection-predicates-native-presquash   # expect EMPTY (tree byte-identical)
```

- [ ] **Step 5: Report and hand off — do NOT push without explicit user approval**

Report: the squashed commit SHA, the review verdict, the three-version test counts, the `NativeOnly` spec
delta, and the backup branch name (keep `-presquash` until the stack merges). Then invoke
`superpowers:finishing-a-development-branch` and present the options. The stacked-PR convention is a
**plain fast-forward** push onto `origin/NativeQueryOngoing` (never `--force`; fetch first and verify the
remote tip is `2a9b56e` and the new commit is its direct child) — **with the user's explicit go-ahead**.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §2 in-scope shapes (`Any(pred)`, `Any()`, negation, nested, through-owned-ref, operand set) | 3 (unit), 4 (functional) |
| §2 success bar (`NativeOnly` routing, `DriverLinq` parity, empty/missing arrays, multi-condition, tracked) | 4 |
| §3 approach A: node + renderer + prefix-rewriter case | 2 |
| §3 rendered-form table (all six rows) | 2 (renderer unit tests pin each) |
| §4 scope-relative path resolution + single-scope guard + `GetContainingElementName()` | 3 |
| §4 element-scoped child translator + the self-enforcing nested-scalar-leaf boundary | 3 (unit), 4 (functional decline) |
| §5 query-dialect classifier, translate-time decline, no converter guard needed | 2 (classifier), 3 (wiring), 1 (spike confirms D1) |
| §6 flip handling + zero spec delta | 5 |
| §7 every deferral asserted as a clean decline | 4 (`All`, `.Count`, primitive, whole-element, field-to-field), 3 (two-scope unit test) |
| §8 all seven spike questions | 1 |
| §9 testing & verification, AGENTS.md note, three-version green | 2, 3, 4, 5 |
| §10 open questions | 1 |

**Placeholder scan:** no TBD/TODO; every code step has complete code; the one deliberate
adapt-to-findings point (Task 3's matcher/unwrapper) states the assumed shape, the fallback instruction, and
what to do if the spike disagrees.

**Type consistency:** `MongoElemMatchExpression(string arrayPath, MongoExpression? elementPredicate, bool
negated)` with `ArrayPath` / `ElementPredicate` / `Negated` is used identically in Tasks 2 and 3;
`IsQueryDialectRenderable` is `public static` in Task 2 and called as
`MongoQueryLanguageRenderer.IsQueryDialectRenderable(...)` in Task 3; `TryResolveOwnedCollectionPath(Expression,
out string?, out IEntityType?)` and `TryMatchAnyMethod(MethodCallExpression, out Expression?, out
LambdaExpression?)` match their call sites.

**Known deviation to expect:** `Post`/`Comment`/`Geo`/`Note` test model classes are declared privately in
three separate test files (renderer unit tests, translator unit tests, functional tests). That duplication is
the existing convention in this repo — each test class owns its model — not an oversight.
