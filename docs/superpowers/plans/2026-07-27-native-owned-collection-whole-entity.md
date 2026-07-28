# Native owned-collection whole-entity queries — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a whole-entity query over an entity with one or more owned **collection** navigations (`OwnsMany`) go native and stream, instead of falling back to driver-LINQ.

**Architecture:** Single gate-predicate generalization in `MongoQueryableMethodTranslatingExpressionVisitor` — the auto-include `Select(x => IncludeExpression(x, ownedNav))` for an embedded owned collection is admitted so the query stays `Route = WholeEntity` and falls through to the existing whole-entity DOM/streaming shaper fold. No pipeline / lowerer / renderer / `StreamingEligibility` / rewriter code changes: the native `{}` pipeline already returns the embedded array, `StreamingEligibility` already admits owned collections, and `MongoStreamingEntityMaterializerRewriter.BuildPlan` already materializes them. Spike-led with a hard GO/NO-GO gate because the owned-collection streaming path may never have run end-to-end for a whole-entity query.

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 via build configs), MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions), TestContainers atlas-local for functional tests.

## Global Constraints

- Multi-EF targeting via build configs `Debug|Release EF8|EF9|EF10`; conditional code uses `EF8`/`EF9`/`EF10` symbols. **This slice adds no `#if`** — identical EF8/EF9/EF10 behavior; all touched types are `internal`.
- `<Nullable>enable</Nullable>` on `src/`; annotate accordingly. Preserve file BOMs.
- Tests run **serially** (assembly-level `DisableTestParallelization`). Each functional test uses a uniquely-named database/collection.
- Run functional/spec tests with both `MONGODB_URI` and `ATLAS_URI` unset (isolated atlas-local container per run).
- The only reliable "goes native" signal for a whole-entity filter/sort/paging shape is **`MongoQueryMode.NativeOnly`**: native ⇒ succeeds, fallback ⇒ throws `NativeTranslationNotSupportedException`. Owned data also has a driver-LINQ oracle, so assert `Native == DriverLinq` parity **and** a `NativeOnly` routing proof.
- **Not a break** (fallback→native, results unchanged, per the provider rubric). Expected to be functional-only; the `NativeOnly` EF10 spec pass-set must stay `2192/2397/19`.
- Delivery: subagent-driven, TDD, stop-for-review after **every** task; squash to one slice commit at the end, plain-FF onto `origin/NativeQueryOngoing`, keep a `-presquash` backup.
- Branch: `EF-322-owned-collection-whole-entity-native` (already created, stacked on `690b487`; spec commit `ee7e43c` is its tip).

## File Structure

- **Modify** `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  — rename `IsOwnedEmbeddedReferenceIncludeSelector` → `IsOwnedEmbeddedIncludeSelector`, drop the `IsCollection` rejection, update the call site (line ~239) and the XML doc-comment. (T2)
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/StreamingEligibility.cs`
  — correct the stale summary doc-comment (lines ~26-31) that claims "No owned collections." No code change. (T2)
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs`
  — correct the stale class-level (lines ~42-43) and `BuildPlan` (lines ~236-237) doc-comments that claim owned collections "are rejected." No code change. (T2)
- **Modify** `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedReferenceWholeEntityTests.cs`
  — flip `Owned_collection_whole_entity_still_falls_back` (line ~152) and `Mixed_owned_reference_and_owned_collection_still_falls_back_under_NativeOnly` (line ~637) from throw-assertions to native-success assertions. (T2 flips the first as the TDD red; T3 flips the second.)
- **Create** `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionWholeEntityTests.cs`
  — positive + edge/parity matrix for owned-collection whole-entity. (T3)
- **Modify** `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeMaterializerOnePassTests.cs`
  — update the stale "STILL falls back" comment (lines ~181-185) on `Owned_reference_and_owned_collection_materialize_correct_nested_values` and strengthen it with a `NativeOnly` routing proof. (T3)
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
  — add the as-built note. (T4)

---

### Task 1: Slice-0 throwaway spike (GO/NO-GO gate)

Throwaway investigation on a scratch branch. **No production edits survive.** The deliverable is a findings doc plus a GO/NO-GO verdict. Do NOT proceed to Task 2 without an explicit GO.

**Files:**
- Create (throwaway): scratch branch `sp-owned-collection-spike` off the current branch tip.
- Create: `.superpowers/sdd/EF-322-owned-collection-whole-entity-spike.md` (findings; committed on the real branch in Task 4's consolidation, or kept gitignored — do not ship it in the squashed slice per convention).

**Interfaces:**
- Consumes: the gate predicate `IsOwnedEmbeddedReferenceIncludeSelector` (`MongoQueryableMethodTranslatingExpressionVisitor.cs:651`) and its call site (line ~239).
- Produces: a verdict (GO/NO-GO), the confirmed streaming floor (streams vs. DOM-only), and the enumerated set of flipping tests, consumed by Tasks 2-4.

- [ ] **Step 1: Create the scratch branch**

```bash
git checkout -b sp-owned-collection-spike
```

- [ ] **Step 2: Pin the `IncludeExpression` shape**

Add a temporary breakpoint-style probe (or a throwaway unit test) that translates `db.Blogs` where `Blog` has `OwnsMany(b => b.Posts)` and inspects the selector reaching `TranslateSelect`. Confirm the auto-include body is an `IncludeExpression { Navigation: <collection nav>, EntityExpression: <lambda param> }` that the existing `while (body is IncludeExpression ...)` loop in the predicate walks — i.e. NOT wrapped in a `CollectionShaperExpression` / `MaterializeCollectionNavigationExpression` that would need separate handling.

Expected: the body is a plain `IncludeExpression` chain; the ONLY thing rejecting it is `if (navigation.IsCollection ...)`.

- [ ] **Step 3: Apply the throwaway gate relaxation**

In the spike branch only, change the predicate loop guard from
`if (navigation.IsCollection || !navigation.IsEmbedded())` to `if (!navigation.IsEmbedded())`.

- [ ] **Step 4: Prove native + streaming end-to-end across the edge matrix**

Spin up a mongod (or use the atlas-local container) and run throwaway functional probes under **both** `MongoQueryMode.NativeOnly` (routing proof) and `MongoQueryMode.Native` vs `DriverLinq` (value parity) for each matrix row in §7 of the spec:
empty collection, multi-element populated, nested owned-ref-in-element, mixed owned-ref + owned-collection, shared-CLR-type owned collection, collection-of-collection (must materialize correctly via DOM), tracking vs `AsNoTracking()`.
For each: compare live materialized values, not just that it runs. For the streaming-eligible rows, confirm the one-pass streaming shaper (not DOM) actually fires (e.g. temporarily assert on the compiled shaper kind, or confirm via the SP7 path) and produces correct arrays.

Expected: all rows correct; eligible shapes stream; collection-of-collection materializes correctly via DOM.

- [ ] **Step 5: Enumerate spec flips and re-verify the NativeOnly sweep**

Build + run the EF10 spec suite under `MONGODB_EF_NATIVE_ONLY=1` on the spike branch:

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory /tmp/sp-owned-coll
```

Expected: pass-set stays `2192/2397/19` (functional-only flip; Northwind has no owned-collection whole-entity coverage). Record any deviation.

- [ ] **Step 6: Record the flipping functional/unit tests**

Confirm the flip set is exactly: `NativeOwnedReferenceWholeEntityTests.Owned_collection_whole_entity_still_falls_back`, `NativeOwnedReferenceWholeEntityTests.Mixed_owned_reference_and_owned_collection_still_falls_back_under_NativeOnly`, and the stale comment on `NativeMaterializerOnePassTests.Owned_reference_and_owned_collection_materialize_correct_nested_values`. Note any additional flips (e.g. `NativeCompositionSeamAuditTests`) surfaced by a broader run.

- [ ] **Step 7: Write the findings doc and give the verdict**

Write `.superpowers/sdd/EF-322-owned-collection-whole-entity-spike.md`: the pinned `IncludeExpression` shape, the confirmed streaming floor (streams / DOM-only), the matrix results, the spec-sweep result, the flip set, and a **GO/NO-GO** verdict for approach A. If NO-GO on streaming (rewriter path incomplete for whole-entity), the verdict must state the DOM-only fallback plan.

- [ ] **Step 8: Discard all spike code**

```bash
git checkout EF-322-owned-collection-whole-entity-native
git branch -D sp-owned-collection-spike
```

**STOP for user review of the spike findings and GO/NO-GO before Task 2.**

---

### Task 2: Gate predicate generalization (TDD) + src/ doc-comment corrections

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedReferenceWholeEntityTests.cs:152` (the canonical flip test — TDD red)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:239,651`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/StreamingEligibility.cs:26-31`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs:42-43,236-237`

**Interfaces:**
- Consumes: the spike's GO verdict and pinned shape.
- Produces: `IsOwnedEmbeddedIncludeSelector(LambdaExpression selector) : bool` (renamed from `IsOwnedEmbeddedReferenceIncludeSelector`), admitting an `IncludeExpression` chain where every navigation is `IsEmbedded()` (collection or reference) and the innermost `EntityExpression` is the lambda parameter. Consumed by `TranslateSelect`'s pass-through guard and by the T3 tests' expectation that owned-collection whole-entity goes native.

- [ ] **Step 1: Flip the canonical guard test to expect native (failing test)**

In `NativeOwnedReferenceWholeEntityTests.cs`, replace the body of `Owned_collection_whole_entity_still_falls_back` (line ~152) and rename it. Replace the whole method with:

```csharp
    [Fact]
    public void Owned_collection_whole_entity_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Owned_collection_whole_entity_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Tags", new BsonArray { new BsonDocument("Name", "a"), new BsonDocument("Name", "b") } }
        });
        var collection = database.MongoDatabase.GetCollection<BlogWithTags>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogWithTagsModel);

        // Under NativeOnly a shape that falls back throws; success here proves the owned-COLLECTION
        // auto-include Select went through the native whole-entity path (EF-322 owned-collection slice).
        var blog = Assert.Single(db.Entities.AsNoTracking().ToList());
        Assert.Equal("Alpha", blog.Title);
        Assert.Equal(["a", "b"], blog.Tags.Select(t => t.Name));
    }
```

(The section header comment above it — "the admit stays narrow — owned COLLECTION and non-owned reference still fall back" — should be narrowed to "non-owned reference still falls back"; leave the `Non_owned_reference_include_still_falls_back` test unchanged.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedReferenceWholeEntityTests.Owned_collection_whole_entity_goes_native"`
Expected: FAIL — throws `NativeTranslationNotSupportedException` (owned collection still falls back before the gate change).

- [ ] **Step 3: Generalize the gate predicate**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, rename the predicate and drop the `IsCollection` rejection. Replace the method (lines ~651-673) with:

```csharp
    private static bool IsOwnedEmbeddedIncludeSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1)
        {
            return false;
        }

        var body = selector.Body;
        var sawInclude = false;

        while (body is IncludeExpression { Navigation: INavigation navigation } include)
        {
            // Admit any EMBEDDED (owned) navigation — a single reference OR a collection. An owned
            // collection embeds as a BSON array in the same document, so the whole-entity DOM/streaming
            // shaper reads it back with no extra pipeline stage, exactly like an owned single reference.
            if (!navigation.IsEmbedded())
            {
                return false;
            }

            sawInclude = true;
            body = include.EntityExpression;
        }

        return sawInclude && body == selector.Parameters[0];
    }
```

Also rewrite the XML `<summary>`/`<para>` doc-comment above it (lines ~624-650) to describe both owned single-reference AND owned collection auto-includes, and to state that the ONLY excluded navigations are non-embedded (reference/cross-collection) ones. Reference the spike doc `.superpowers/sdd/EF-322-owned-collection-whole-entity-spike.md`.

- [ ] **Step 4: Update the call site**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs` at line ~239, change:

```csharp
                 && !IsOwnedEmbeddedReferenceIncludeSelector(selector))
```

to:

```csharp
                 && !IsOwnedEmbeddedIncludeSelector(selector))
```

- [ ] **Step 5: Correct the two stale src/ doc-comments**

In `StreamingEligibility.cs`, fix the summary (lines ~26-31): it currently says "navigations are only single (reference) owned sub-documents … No owned collections." Change it to state that owned collections ARE eligible (only non-owned collections and collection-of-collection are rejected), matching the code below it.

In `MongoStreamingEntityMaterializerRewriter.cs`, fix the class-level comment (lines ~42-43: "Owned collections … are rejected") and the `BuildPlan` comment (lines ~236-237: "Rejects owned collections") to state that owned collections ARE materialized (via `CollectionPlan`/fill-loop) and only NON-owned collections throw `NativeTranslationNotSupportedException`.

- [ ] **Step 6: Run the flipped test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedReferenceWholeEntityTests.Owned_collection_whole_entity_goes_native"`
Expected: PASS.

- [ ] **Step 7: Run the surrounding gate-test class to catch regressions**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedReferenceWholeEntityTests"`
Expected: PASS except `Mixed_owned_reference_and_owned_collection_still_falls_back_under_NativeOnly` (now fails — the mixed chain goes native after this change; it is flipped in Task 3). Confirm no OTHER test in the class regressed.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/StreamingEligibility.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedReferenceWholeEntityTests.cs
git commit -m "EF-322: admit owned-collection whole-entity in the native gate"
```

**STOP for user review.**

---

### Task 3: Edge/parity matrix + flip the mixed test + refresh the one-pass comment

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionWholeEntityTests.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedReferenceWholeEntityTests.cs` (the mixed test, line ~637)
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeMaterializerOnePassTests.cs` (comment on line ~181-185)

**Interfaces:**
- Consumes: `IsOwnedEmbeddedIncludeSelector` (native routing from Task 2); the spike's confirmed edge-case expected values.
- Produces: functional coverage proving native routing + `Native == DriverLinq` parity for owned-collection whole-entity.

- [ ] **Step 1: Write the new edge/parity test file**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionWholeEntityTests.cs`. Preserve the file BOM and copyright header (copy the header block verbatim from `NativeOwnedReferenceWholeEntityTests.cs`). Full body:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322 owned-collection slice: a whole-entity query over an entity with an owned COLLECTION navigation
/// (OwnsMany, auto-included eagerly) goes native and streams — the gate predicate
/// IsOwnedEmbeddedIncludeSelector admits the synthetic Select(x => IncludeExpression(x, ownedCollection)).
/// Owned data round-trips through the driver, so each case asserts Native == DriverLinq parity plus a
/// NativeOnly routing proof.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionWholeEntityTests(TemporaryDatabaseFixture database)
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
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
        public int Rank { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] docs)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(docs);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument BlogDoc(string title, params (string heading, int rank)[] posts)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Title", title },
            { "Posts", new BsonArray(posts.Select(p =>
                new BsonDocument { { "Heading", p.heading }, { "Rank", p.rank } })) }
        };

    // Runs the query under NativeOnly (routing proof) and under Native vs DriverLinq (parity), returning
    // the NativeOnly result for the caller to assert values on.
    private List<Blog> AssertNativeAndParity(IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        List<Blog> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = query(db.Entities.AsNoTracking()).ToList();
        }

        List<Blog> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList();
        }

        Assert.Equal(driver.Count, nativeOnly.Count);
        for (var i = 0; i < driver.Count; i++)
        {
            Assert.Equal(driver[i].Title, nativeOnly[i].Title);
            Assert.Equal(
                driver[i].Posts.Select(p => (p.Heading, p.Rank)),
                nativeOnly[i].Posts.Select(p => (p.Heading, p.Rank)));
        }

        return nativeOnly;
    }

    [Fact]
    public void Populated_owned_collection_goes_native_and_preserves_order()
    {
        var collection = Seed(nameof(Populated_owned_collection_goes_native_and_preserves_order),
            BlogDoc("Alpha", ("intro", 1), ("body", 2), ("outro", 3)));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q));

        Assert.Equal("Alpha", blog.Title);
        Assert.Equal(["intro", "body", "outro"], blog.Posts.Select(p => p.Heading));
        Assert.Equal([1, 2, 3], blog.Posts.Select(p => p.Rank));
    }

    [Fact]
    public void Empty_owned_collection_materializes_empty_list()
    {
        var collection = Seed(nameof(Empty_owned_collection_materializes_empty_list),
            BlogDoc("Empty"));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q));

        Assert.Equal("Empty", blog.Title);
        Assert.Empty(blog.Posts);
    }

    [Fact]
    public void Owned_collection_with_root_where_goes_native()
    {
        var collection = Seed(nameof(Owned_collection_with_root_where_goes_native),
            BlogDoc("Alpha", ("a", 1)), BlogDoc("Beta", ("b", 2)));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q.Where(b => b.Title == "Beta")));

        Assert.Equal("Beta", blog.Title);
        Assert.Equal(["b"], blog.Posts.Select(p => p.Heading));
    }

    // ── Mixed owned reference + owned collection on the same entity: the whole chain now goes native ──

    private class Shop
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ShopAddress Address { get; set; } = null!;
        public List<Item> Items { get; set; } = [];
    }

    private class ShopAddress
    {
        public string City { get; set; } = "";
    }

    private class Item
    {
        public string Sku { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> ShopModel = mb =>
    {
        mb.Entity<Shop>().OwnsOne(s => s.Address);
        mb.Entity<Shop>().OwnsMany(s => s.Items);
    };

    [Fact]
    public void Mixed_owned_reference_and_owned_collection_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Mixed_owned_reference_and_owned_collection_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Acme" },
            { "Address", new BsonDocument { { "City", "NYC" } } },
            { "Items", new BsonArray { new BsonDocument("Sku", "A1"), new BsonDocument("Sku", "B2") } }
        });
        var collection = database.MongoDatabase.GetCollection<Shop>(coll.CollectionNamespace.CollectionName);

        // Routing proof: NativeOnly succeeds (mixed owned-ref + owned-collection chain admitted as a whole).
        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, ShopModel))
        {
            var shop = Assert.Single(native.Entities.AsNoTracking().ToList());
            Assert.Equal("NYC", shop.Address.City);
            Assert.Equal(["A1", "B2"], shop.Items.Select(i => i.Sku));
        }

        // Parity with driver-LINQ.
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, ShopModel);
        var shopD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal("NYC", shopD.Address.City);
        Assert.Equal(["A1", "B2"], shopD.Items.Select(i => i.Sku));
    }

    // ── Nested owned reference inside a collection element ──

    private class Team
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Member> Members { get; set; } = [];
    }

    private class Member
    {
        public string Name { get; set; } = "";
        public Badge Badge { get; set; } = null!;
    }

    private class Badge
    {
        public string Code { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> TeamModel = mb =>
        mb.Entity<Team>().OwnsMany(t => t.Members, m => m.OwnsOne(x => x.Badge));

    [Fact]
    public void Owned_collection_element_with_nested_owned_reference_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Owned_collection_element_with_nested_owned_reference_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Red" },
            { "Members", new BsonArray
                {
                    new BsonDocument { { "Name", "Ann" }, { "Badge", new BsonDocument("Code", "X1") } },
                    new BsonDocument { { "Name", "Bob" }, { "Badge", new BsonDocument("Code", "X2") } }
                }
            }
        });
        var collection = database.MongoDatabase.GetCollection<Team>(coll.CollectionNamespace.CollectionName);

        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, TeamModel))
        {
            var team = Assert.Single(native.Entities.AsNoTracking().ToList());
            Assert.Equal(["Ann", "Bob"], team.Members.Select(m => m.Name));
            Assert.Equal(["X1", "X2"], team.Members.Select(m => m.Badge.Code));
        }

        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, TeamModel);
        var teamD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal(["Ann", "Bob"], teamD.Members.Select(m => m.Name));
        Assert.Equal(["X1", "X2"], teamD.Members.Select(m => m.Badge.Code));
    }

    // ── Collection-of-collection: still correct, via DOM (streaming-ineligible) ──

    private class Library
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Shelf> Shelves { get; set; } = [];
    }

    private class Shelf
    {
        public string Label { get; set; } = "";
        public List<Book> Books { get; set; } = [];
    }

    private class Book
    {
        public string Title { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> LibraryModel = mb =>
        mb.Entity<Library>().OwnsMany(l => l.Shelves, s => s.OwnsMany(x => x.Books));

    [Fact]
    public void Collection_of_collection_goes_native_via_dom_and_materializes_correctly()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Collection_of_collection_goes_native_via_dom_and_materializes_correctly)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Main" },
            { "Shelves", new BsonArray
                {
                    new BsonDocument
                    {
                        { "Label", "SciFi" },
                        { "Books", new BsonArray { new BsonDocument("Title", "Dune") } }
                    }
                }
            }
        });
        var collection = database.MongoDatabase.GetCollection<Library>(coll.CollectionNamespace.CollectionName);

        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, LibraryModel))
        {
            var lib = Assert.Single(native.Entities.AsNoTracking().ToList());
            var shelf = Assert.Single(lib.Shelves);
            Assert.Equal("SciFi", shelf.Label);
            Assert.Equal(["Dune"], shelf.Books.Select(b => b.Title));
        }

        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, LibraryModel);
        var libD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal(["Dune"], Assert.Single(libD.Shelves).Books.Select(b => b.Title));
    }
}
```

Note: if the Task 1 spike found that collection-of-collection whole-entity does NOT go native under `NativeOnly` (only owned-collection non-nested does), change `Collection_of_collection_...` to assert `Native == DriverLinq` parity under `Native` mode instead of a `NativeOnly` routing proof, and rename it accordingly. Encode the spike's actual finding here.

- [ ] **Step 2: Run the new test file**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionWholeEntityTests"`
Expected: PASS (all).

- [ ] **Step 3: Flip the mixed guard test in the reference file**

In `NativeOwnedReferenceWholeEntityTests.cs`, replace `Mixed_owned_reference_and_owned_collection_still_falls_back_under_NativeOnly` (line ~637) with a native-success assertion:

```csharp
    [Fact]
    public void Mixed_owned_reference_and_owned_collection_goes_native_under_NativeOnly()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Mixed_owned_reference_and_owned_collection_goes_native_under_NativeOnly)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } },
            { "Tags", new BsonArray { new BsonDocument("Name", "a") } }
        });
        var collection = database.MongoDatabase.GetCollection<BlogMixed>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogMixedModel);

        // The admit predicate now accepts EVERY embedded navigation in the auto-include chain — an owned
        // reference (Address) mixed with an owned COLLECTION (Tags) on the same root is admitted as a whole
        // and goes native (EF-322 owned-collection slice; previously this fell back).
        var blog = Assert.Single(db.Entities.AsNoTracking().ToList());
        Assert.Equal("NYC", blog.Address.City);
        Assert.Equal(["a"], blog.Tags.Select(t => t.Name));
    }
```

Update the section-header comment above it (lines ~615-618) to reflect that the mixed shape now goes native.

- [ ] **Step 4: Refresh the stale one-pass comment and strengthen it**

In `NativeMaterializerOnePassTests.cs`, the comment block above `Owned_reference_and_owned_collection_materialize_correct_nested_values` (lines ~181-185) says this shape "STILL falls back to driver-LINQ … asserts Native↔DriverLinq PARITY … rather than a NativeOnly routing assertion." Rewrite it to say the shape now goes native (EF-322 owned-collection slice) and the test still asserts `Native == DriverLinq` parity as a materialization guard. Then add a `NativeOnly` routing proof at the end of the test body, immediately before its closing brace — after the existing parity assertions:

```csharp
        // EF-322 owned-collection slice: this mixed owned-ref + owned-collection shape now ALSO routes
        // native — prove it (NativeOnly succeeds instead of throwing) in addition to the parity guard above.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            AssertNesting(nativeOnly.Entities.AsNoTracking().ToList(), id1);
        }
```

(Confirm `AssertNesting`, `model`, `id1`, and `CreateContext` are in scope in that test method — they are, per the existing body.)

- [ ] **Step 5: Run both modified test classes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedReferenceWholeEntityTests|FullyQualifiedName~NativeMaterializerOnePassTests"`
Expected: PASS (all).

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionWholeEntityTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedReferenceWholeEntityTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeMaterializerOnePassTests.cs
git commit -m "EF-322: owned-collection whole-entity parity/edge tests + flip guards"
```

**STOP for user review.**

---

### Task 4: Validate across all three EF versions + as-built docs

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: everything from Tasks 2-3.
- Produces: the shipped, verified slice + AGENTS.md as-built note.

- [ ] **Step 1: Run the full test suite for all three EF versions**

Invoke the `/test-all` skill (build + test EF8, EF9, EF10 in parallel).
Expected: 0 failures on all three. Record the per-version pass counts (baseline reference: EF8 ~7402, EF9 ~7763, EF10 ~7360 from the prior slice — this slice adds functional tests, so counts increase by the net new/flipped tests; there must be **zero failures** and no regression relative to baseline).

- [ ] **Step 2: Re-baseline the NativeOnly EF10 spec sweep**

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory /tmp/owned-coll-final
```
Expected: `2192` passed / `2397` failed / `19` skipped — **unchanged** from baseline (functional-only eligibility change; Northwind has no owned-collection whole-entity coverage). If the count differs, investigate before proceeding — any spec flip must be a correctness-verified native gain, not a regression.

- [ ] **Step 3: Write the AGENTS.md as-built note**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, add a note (following the style of the existing "Owned single-reference whole-entity queries (EF-322 Task 2)" note) titled for the owned-collection slice: root cause (gate rejected `IsCollection`), the fix (`IsOwnedEmbeddedIncludeSelector` admits any embedded nav, collection or reference), the "no downstream change — `StreamingEligibility` + rewriter already handle owned collections; the doc-comments were stale and are corrected" finding, what stays excluded (non-owned/reference collection Include; owned-collection sub-property predicates/projections), the streaming-vs-DOM split (collection-of-collection → DOM), and the not-a-break / functional-only-flip disposition. Also update the existing owned-single-reference note's "Deliberately narrow — what stays excluded" paragraph, which currently claims an owned COLLECTION "is rejected" / "still falling back" — that is now false; point it at the new note.

- [ ] **Step 4: Commit the docs**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-322: owned-collection whole-entity as-built note"
```

**STOP for user review. Then finalize:** squash Tasks 2-4 into one slice commit `EF-322: owned-collection whole-entity goes native (+ streams)` (exclude the `.superpowers/sdd/` spike doc per convention), verify the squashed tree is byte-identical to a `-presquash` backup, fetch-verify fast-forward, and plain-FF push `690b487..<squashed>` onto `origin/NativeQueryOngoing`. Keep `EF-322-owned-collection-whole-entity-native-presquash` until merge.

---

## Self-Review

**Spec coverage:**
- §2 root cause (gate rejects `IsCollection`) → Task 2 Step 3. ✓
- §3 downstream already built + stale doc-comments → Task 2 Steps 5 (comment fixes) + Task 1 (spike proves the path fires). ✓
- §4 scope in/out → Task 3 tests cover owned collection, mixed, nested, collection-of-collection; sub-property predicates explicitly excluded (no task adds them). ✓
- §5 approach A (generalize the one predicate) → Task 2 Step 3. ✓
- §6 spike-led method, T1-T4 → Tasks 1-4. ✓
- §7 edge/parity matrix → Task 3 Step 1 (empty, populated/order, mixed, nested, collection-of-collection) + Task 2 (basic native routing) + the existing tracking-covered tests. ✓
- §8 not-a-break / NativeOnly sweep `2192/2397/19` → Task 4 Step 2. ✓
- §9 risks (doc/code contradiction, IncludeExpression shape, list allocation) → Task 1 spike gate. ✓

**Placeholder scan:** no TBD/TODO; all code steps show complete code; the one spike-dependent branch (collection-of-collection routing) gives a concrete default assertion with an explicit "encode the spike's actual finding" instruction, not a blank. ✓

**Type consistency:** predicate renamed consistently `IsOwnedEmbeddedReferenceIncludeSelector` → `IsOwnedEmbeddedIncludeSelector` at both definition (Task 2 Step 3) and call site (Task 2 Step 4). Test helper names (`CreateContext`, `AssertNativeAndParity`, `BlogModel`, `ShopModel`, `TeamModel`, `LibraryModel`, `BlogDoc`, `Seed`) are self-consistent within the new file. Flipped tests reference existing fixtures (`BlogWithTags`, `BlogWithTagsModel`, `BlogMixed`, `BlogMixedModel`, `AssertNesting`) confirmed present in the target files. ✓
