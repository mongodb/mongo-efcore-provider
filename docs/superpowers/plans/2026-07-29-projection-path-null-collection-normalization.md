# EF-358 — Projection-Path Null-Collection Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the projection path materialize a missing or explicitly-BSON-null embedded array as an **empty collection** rather than `null`, matching what whole-entity materialization of the same document already does — closing EF-358 and fully closing EF-357.

**Architecture:** Two edits in two files. `BsonDocumentInjectingExpressionVisitor` stops collapsing a null `BsonArray` to a null collection (a pure deletion of its guard conditional), and `MongoProjectionBindingRemovingExpressionVisitor` coalesces the array to an empty `BsonArray` at the point of use. The existing populated path then does all the work: `PopulateCollection` builds the collection through the navigation's own `IClrCollectionAccessor`, so the CLR collection type is correct for free and no second empty-collection construction site is introduced.

**Tech Stack:** C#, .NET 8 (EF8/EF9) / .NET 10 (EF10), EF Core 8/9/10, MongoDB C# driver, xUnit. Expression-tree visitors under `src/MongoDB.EntityFrameworkCore/Query/Visitors/`.

**Design doc:** `docs/superpowers/specs/2026-07-29-projection-path-null-collection-normalization-design.md`.

## Global Constraints

- **Branch:** `EF-358`, stacked on the unmerged EF-322 native stack. Base commit `cfe873e` (= `origin/NativeQueryOngoing`). Do **not** rebase or force-push.
- **Nullable:** all `src/` code is under `<Nullable>enable</Nullable>`. Annotate accordingly.
- **No `#if`:** the touched types are `internal` and the behaviour is identical on EF8/EF9/EF10. Do not add EF-version conditionals.
- **No public API change, no annotation-key change, no persisted-document-shape change.** This is a read path only.
- **No `BREAKING-CHANGES.md` entry** — decided by the branch owner; rationale is recorded in design §1.3. Do not add one.
- **Preserve file BOMs** when editing existing files.
- **Tests use plain xUnit `Assert.*`.** FluentAssertions is not referenced in the test projects despite what the root `AGENTS.md` says about the stack.
- **Run tests with both `MONGODB_URI` and `ATLAS_URI` unset** so each run gets its own isolated `mongodb/mongodb-atlas-local` container (Docker required).
- **Cross-references in comments must be quote-anchored, not line-numbered.** Precision line references have broken *within* a single slice on this stack.
- **Do not quote bare pass counts** as the record of a run. Record what was run and the outcome.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs` | Wraps entity/collection shapers in `BsonDocument`/`BsonArray` local variables | **Modify** — delete the null-collapse conditional in the `CollectionShaperExpression` case |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` | Turns projection bindings into concrete BSON reads; owns collection construction | **Modify** — coalesce `bsonArrayExpression` in the `CollectionShaperExpression` case; add a contract comment at `VisitBinary`'s hard cast |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs` | End-to-end normalization coverage: array state matrix, collection-type breadth, nesting, whole-entity differential oracle | **Create** |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` | Owned-collection count coverage | **Modify** — invert the EF-357 residual test |
| `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | Query-area invariants | **Modify** — rewrite the now-false projection-path note |
| `docs/native-query-status-EF-322.md` | Epic status report | **Modify** — §4 paragraph, §6 EF-357/EF-358 rows |
| `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md`, `docs/superpowers/plans/2026-07-28-native-owned-collection-count-projections.md` | Slice-7 design/plan | **Modify** — prose **and** verbatim code blocks |

---

### Task 1: Spike — measure the blast radius, then GO/NO-GO

**Throwaway.** No production change survives this task. Its output is a findings document plus a decision.

**Files:**
- Create (temporary, deleted at end of task): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef358SpikeTests.cs`
- Create (kept): `docs/superpowers/specs/2026-07-29-projection-path-null-collection-normalization-spike-findings.md`

**Interfaces:**
- Consumes: nothing.
- Produces: the findings document. Task 2 relies on its answers to Q1 (which shapes are in the flip surface) and Q2 (`FindCollectionShaper` survives the deletion).

- [ ] **Step 1: Write the probe test class**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef358SpikeTests.cs`. This is a measurement harness, not an assertion suite — each probe **prints** what it observes and swallows exceptions so one failure does not hide the rest.

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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

// EF-358 SPIKE — THROWAWAY. Delete before Task 1 is committed.
[XUnitCollection("QueryTests")]
public class Ef358SpikeTests(TemporaryDatabaseFixture database, ITestOutputHelper output)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = [];
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    private static SingleEntityDbContext<Blog> CreateContext(
        IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", title } };
        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    private static BsonArray PostsOf(params string[] headings)
        => new(headings.Select(h => new BsonDocument
        {
            { "Heading", h }, { "Comments", new BsonArray { new BsonDocument { { "Text", "c" } } } }
        }));

    private IMongoCollection<Blog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        coll.InsertMany([
            Row("two", PostsOf("a", "b")),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private void Probe(string label, Func<string> body)
    {
        try
        {
            output.WriteLine($"{label} => {body()}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"{label} => THREW {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    [Fact]
    public void Q1_which_shapes_reach_the_null_collapse()
    {
        var collection = Seed(nameof(Q1_which_shapes_reach_the_null_collapse));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            output.WriteLine($"===== {mode} =====");

            Probe("array projection  Select(b => b.Posts)", () =>
            {
                using var db = CreateContext(collection, mode);
                var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();
                return string.Join(", ", rows.Select(p => p is null ? "null" : p.Count.ToString()));
            });

            Probe("array leaf        Select(b => new { b.Title, b.Posts })", () =>
            {
                using var db = CreateContext(collection, mode);
                var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => new { b.Title, b.Posts }).ToList();
                return string.Join(", ", rows.Select(r => $"{r.Title}:{(r.Posts is null ? "null" : r.Posts.Count.ToString())}"));
            });

            Probe("bare count        Select(b => b.Posts.Count)", () =>
            {
                using var db = CreateContext(collection, mode);
                return string.Join(", ", db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList());
            });

            Probe("nested inner      Select(b => b.Posts) then inner Comments", () =>
            {
                using var db = CreateContext(collection, mode);
                var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();
                return string.Join(", ", rows.Select(p =>
                    p is null ? "null" : string.Join("/", p.Select(x => x.Comments is null ? "null" : x.Comments.Count.ToString()))));
            });

            Probe("CONTROL whole-entity", () =>
            {
                using var db = CreateContext(collection, mode);
                return string.Join(", ", db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList()
                    .Select(b => b.Posts is null ? "null" : b.Posts.Count.ToString()));
            });

            Probe("CONTROL Include(b => b.Posts)", () =>
            {
                using var db = CreateContext(collection, mode);
                return string.Join(", ", db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Include(b => b.Posts).ToList()
                    .Select(b => b.Posts is null ? "null" : b.Posts.Count.ToString()));
            });
        }
    }
}
```

- [ ] **Step 2: Build and run the probe**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Ef358SpikeTests" --logger "console;verbosity=detailed"
```

Expected: the probe passes (it asserts nothing) and the console shows one line per shape per mode. Rows are ordered `empty, missing, null, two` by title.

Record every line verbatim. The two things to read off it:

- **The flip surface.** Which shapes report `null` for the `missing`/`null` rows today, and in which modes. Anything reporting `null` changes behaviour in Task 2; anything reporting a number does not.
- **Whether `DriverLinq` is in the flip surface at all** for the array projection, or whether the driver renders its own projection and never reaches this site (design §4.1 Q1).

- [ ] **Step 3: Answer Q2 — does `FindCollectionShaper` survive the deletion?**

Temporarily apply **only** Edit 1 from design §3 (delete the conditional in `BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case, leaving the `Expression.Assign` byte-identical). Do **not** apply Edit 2 yet.

Then run the owned-collection whole-entity streaming coverage, which is what exercises `MongoStreamingEntityMaterializerRewriter.FindCollectionShaper`:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeMaterializerOnePassTests|FullyQualifiedName~NativeOwnedCollectionWholeEntity|FullyQualifiedName~StreamingEligibilityTests"
```

Expected: green. `FindCollectionShaper`'s `BlockExpression` arm walks expressions **backwards**, so it finds the shaper at the block's last position once the `ConditionalExpression` wrapper is gone. If instead it throws `NativeTranslationNotSupportedException: Unexpected owned-collection materializer shape`, that arm needs an update and Task 2 must cover it — record which.

- [ ] **Step 4: Revert the temporary production edit**

```bash
git checkout -- src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs
git diff --stat src/    # expected: empty
```

- [ ] **Step 5: Write the findings document**

Create `docs/superpowers/specs/2026-07-29-projection-path-null-collection-normalization-spike-findings.md` containing:

1. The verbatim probe output, as a table: shape × mode × the four array states.
2. **The flip surface** — the explicit list of shapes whose results change in Task 2, and the modes affected.
3. **Q1 answer** — does `DriverLinq`'s array projection reach this site?
4. **Q2 answer** — did the streaming filter stay green under Edit 1 alone? If not, what threw.
4b. **Explicitly noted as NOT covered here:** the design's §4.1 table also lists a projected **reference**
   collection as a control. This probe's model is a single owned-collection entity, so that control needs a
   two-collection fixture and is deferred to Task 2's `CrossCollectionIncludeTests` /
   `QueryModeGateIncludeTests` run. Record that it is deferred rather than silently dropping it.
5. **GO / NO-GO**, with reasoning. NO-GO if a control moved (whole-entity or `Include` changed behaviour under Edit 1 alone), or if the flip surface is materially wider than the design's §4.1 table predicted — either means the design needs revisiting before code lands.

Anything the probe measured that contradicts the design doc goes in this document **and** gets corrected in the design doc in the same task. Do not carry a refuted claim forward.

- [ ] **Step 6: Delete the probe and commit**

```bash
rm tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef358SpikeTests.cs
git add docs/superpowers/specs/
git commit -m "EF-358: spike - measure the projection-path null-collapse blast radius"
```

**STOP.** Report the findings and the GO/NO-GO to the user before starting Task 2.

---

### Task 2: The fix, the EF-357 flip, and the mutation pin

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs` (the `CollectionShaperExpression` case)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` (the `CollectionShaperExpression` case in `VisitExtension`; a comment at `VisitBinary`)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` (invert the EF-357 residual test)

**Interfaces:**
- Consumes: Task 1's findings (the flip surface).
- Produces: the test class `ProjectedCollectionNormalizationTests` with helpers `Row(string title, BsonValue? posts)`, `PostsOf(params string[] headings)`, `Seed(string name)`, `CreateContext(IMongoCollection<Blog>, MongoQueryMode)`, and the nested model types `Blog` / `Post` / `Comment`. Task 3 extends this same class and reuses all of them.

- [ ] **Step 1: Write the failing test — the mutation pin**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs`:

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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-358: the PROJECTION path normalizes a missing or explicitly-BSON-null embedded array to an EMPTY
/// collection, matching what whole-entity materialization of the same document already does. It used to
/// materialize null, which made <c>Select(b =&gt; b.Posts)</c> return null for those rows and
/// <c>Select(b =&gt; b.Posts.Count)</c> throw ArgumentNullException from Enumerable.Count(null) — the residual
/// that kept EF-357 only partially closed.
/// </summary>
[XUnitCollection("QueryTests")]
public class ProjectedCollectionNormalizationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // Nullable ON PURPOSE: a missing stored element field must materialize rather than throw, or the
        // ragged-array states cannot be exercised at all. Same reasoning as NativeOwnedCollectionCountTests.Post.
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = [];
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    private static SingleEntityDbContext<Blog> CreateContext(
        IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // A null `posts` means the FIELD IS ABSENT; BsonNull.Value means the field is present and explicitly null.
    // Those are the two states whole-entity materialization normalizes and the projection path used not to.
    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", title } };
        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    private static BsonArray PostsOf(params string[] headings)
        => new(headings.Select(h => new BsonDocument
        {
            { "Heading", h }, { "Comments", new BsonArray() }
        }));

    // Titles are chosen so alphabetical order is deterministic and independent of insertion order:
    // empty < missing < null < two.
    private IMongoCollection<Blog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            Row("two", PostsOf("a", "b")),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection()
    {
        // THE MUTATION PIN for EF-358. It asserts returned DATA, not an exception type, deliberately: an
        // assertion pinning only an exception type usually cannot prove WHICH guard fired, and several
        // teeth-checks on this stack were vacuous for exactly that reason. Revert the Coalesce in
        // MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case and the
        // Assert.NotNull below fails for the `missing` and `null` rows.
        var collection = Seed(nameof(Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }

    [Fact]
    public void Bare_count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // EF-357, now FULLY closed. Owned-data slice 7 removed the translation-time ArgumentException; this
        // removes the materialization-time ArgumentNullException (Enumerable.Count(null)) that kept it partial.
        // The count itself is still folded CLIENT-SIDE here — a bare-scalar projection body never populates
        // Select.Projection, which is the SP3-wide bare-scalar boundary, not anything count-specific — so this
        // is exercising the normalized empty collection, not a server-side $size.
        var collection = Seed(nameof(Bare_count_projection_returns_zero_for_a_missing_or_null_array));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode);

            var counts = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();

            Assert.Equal([0, 0, 0, 2], counts);
        }
    }

    [Fact]
    public void Whole_entity_materialization_is_unchanged()
    {
        // CONTROL. Whole-entity materialization never reached the null-collapse — it fills collections through
        // IClrCollectionAccessor.Add on an already-created collection, so an absent array simply contributes no
        // elements. Its agreement with the projection path above IS the property EF-358 restores, so it has to
        // be asserted here rather than assumed.
        var collection = Seed(nameof(Whole_entity_materialization_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Collection_include_is_unchanged()
    {
        // CONTROL. An owned collection Include is read off the same document; a cross-collection $lookup always
        // writes an array. Neither should move.
        var collection = Seed(nameof(Collection_include_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).Include(b => b.Posts).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Array_projection_normalizes_for_a_tracking_query()
    {
        var collection = Seed(nameof(Array_projection_normalizes_for_a_tracking_query));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var rows = db.Entities.OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail for the right reason**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~ProjectedCollectionNormalizationTests"
```

Expected:
- `Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection` — **FAIL** (`Assert.NotNull` on the `missing`/`null` rows).
- `Bare_count_projection_returns_zero_for_a_missing_or_null_array` — **FAIL** (`ArgumentNullException`).
- `Array_projection_normalizes_for_a_tracking_query` — **FAIL**.
- `Whole_entity_materialization_is_unchanged` and `Collection_include_is_unchanged` — **PASS already**. These are controls; if either fails here, stop and report, because the seed or model is wrong rather than the production code.

- [ ] **Step 3: Apply Edit 1 — delete the null-collapse conditional**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs`, replace the `CollectionShaperExpression` case's `expressions` initializer. The `Expression.Assign` stays **byte-identical**:

```csharp
                    // EF-358: a MISSING or explicitly-BSON-null stored array normalizes to an EMPTY collection
                    // rather than collapsing the whole collection shaper to null. This used to be an
                    // Expression.Condition whose null branch was Expression.Constant(null,
                    // collectionShaperExpression.Type) — which is what made a projected collection come back
                    // null for those rows while whole-entity materialization of the SAME document yielded an
                    // empty collection. The normalization itself lives in
                    // MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case, which
                    // coalesces the array at its point of use; see the comment there.
                    //
                    // The Expression.Assign below must keep a UnaryExpression right-hand side: the removing
                    // visitor's VisitBinary hard-casts `binaryExpression.Right` to UnaryExpression for any
                    // Assign whose left side is a BsonDocument- or BsonArray-typed ParameterExpression. Folding
                    // the coalesce in here (Coalesce is a BinaryExpression) throws InvalidCastException for
                    // EVERY collection shaper in every query mode, not just a ragged row.
                    var expressions = new List<Expression>
                    {
                        Expression.Assign(
                            arrayVariable,
                            Expression.TypeAs(
                                collectionShaperExpression.Projection,
                                typeof(BsonArray))),
                        collectionShaperExpression
                    };
```

- [ ] **Step 4: Apply Edit 2 — coalesce at the point of use**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`, in the `CollectionShaperExpression` case of `VisitExtension`, insert the coalesce immediately after the `if`/`else` that resolves `bsonArrayExpression` and `arrayName` — i.e. directly before the `jObjectParameter` declaration:

```csharp
        // EF-358: normalize a MISSING or explicitly-BSON-null stored array to an EMPTY BsonArray, so the
        // shaper below enumerates nothing and PopulateCollection returns an EMPTY collection instead of the
        // null the injecting visitor used to short-circuit to. Whole-entity materialization already behaves
        // this way (it fills collections via IClrCollectionAccessor.Add, so an absent array contributes no
        // elements); this makes the projection path agree.
        //
        // Coalescing at the POINT OF USE, rather than at either assignment site, is what makes one line cover
        // BOTH array sources above: the bound _projectionBindings variable, and the nested
        // collection-in-collection branch that reads from the parent document via CreateGetBsonArray.
        //
        // The empty collection is built by PopulateCollection through the navigation's OWN
        // IClrCollectionAccessor, so a non-List collection navigation (HashSet<T>, a custom collection) gets
        // the right CLR type for free and there is no second empty-collection construction site.
        //
        // Note TypeAs yields null both for an ABSENT element and for a present-but-not-an-array element, so
        // this treats the two alike. Both produced null before, so that is not a regression, but a document
        // storing a scalar where an array belongs now materializes as empty rather than surfacing as a type
        // error. Recorded deliberately; see the design doc's "One conflation" section.
        bsonArrayExpression = Expression.Coalesce(bsonArrayExpression, Expression.New(typeof(BsonArray)));

        var jObjectParameter = Expression.Parameter(typeof(BsonDocument), arrayName + "Object");
```

- [ ] **Step 5: Document the cross-visitor contract at the hard cast**

Still in `MongoProjectionBindingRemovingExpressionVisitor.cs`, in `VisitBinary`, add a comment immediately above the `((UnaryExpression)binaryExpression.Right).Operand` line. This is the constraint that a future editor is most likely to break:

```csharp
                    // CONTRACT with BsonDocumentInjectingExpressionVisitor: the right-hand side of a
                    // BsonDocument/BsonArray variable assignment is always an Expression.TypeAs — a
                    // UnaryExpression — so this cast is safe. If that visitor ever needs a different node
                    // shape there (a Coalesce, a New), this cast must be widened in the SAME change, or every
                    // entity and collection shaper throws InvalidCastException in every query mode. EF-358
                    // ran into exactly this and put its normalization at the point of use instead.
                    var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
```

- [ ] **Step 6: Run the new tests to verify they pass**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~ProjectedCollectionNormalizationTests"
```

Expected: all five PASS.

- [ ] **Step 7: Invert the EF-357 residual test**

In `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs`, replace `Bare_embedded_collection_Count_projection_still_throws_for_a_missing_or_null_array` entirely — name and body — with:

```csharp
    [Fact]
    public void Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // EF-357 is now FULLY closed, and this test records the second half of that closure. Owned-data slice 7
        // removed the TRANSLATION-time ArgumentException; EF-358 removed the MATERIALIZATION-time
        // ArgumentNullException this test used to assert, by making the projection path normalize a missing or
        // explicitly-null stored array to an empty collection — matching whole-entity materialization of the
        // same document, which always did.
        //
        // The shape is still NOT native: a bare-scalar projection body never populates Select.Projection, which
        // is the SP3-wide bare-scalar boundary rather than anything count-specific, so the count is still folded
        // client-side over aggregate([]) — see
        // Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays, which pins that
        // MQL. What changed is only that the client-side fold now receives an empty collection instead of null.
        //
        // The native WRAPPED form was always correct for all three states via $ifNull and is unaffected.
        var collection = SeedLengths(
            nameof(Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);

            var counts = db.Entities.AsNoTracking()
                .Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();

            Assert.Equal([0, 0, 0, 1, 2, 3], counts);
        }
    }
```

Note the expected set: `SeedLengths` seeds `len0, len1, len2, len3, missing, null`, so sorted counts are `0, 0, 0, 1, 2, 3` — the three zeros being `len0`, `missing` and `null`.

- [ ] **Step 8: Update the sibling test's cross-reference**

`Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays` has a comment referring to "the residual pinned by the companion test below". Update that wording to point at the renamed test and to say EF-357 is fully closed. Search for the phrase rather than a line number:

```bash
grep -n "residual pinned by the companion test" \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
grep -n "EF-357" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
```

Every EF-357 mention in this file must now read as fully closed rather than partial.

- [ ] **Step 9: Run the named non-regression checks individually**

These are stated acceptance criteria, not incidental:

```bash
# The deliberate nullability-aware null precedent — the single thing this must NOT touch.
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~PrimitiveCollectionTests"

# Streaming / whole-entity owned-collection materialization.
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeMaterializerOnePassTests|FullyQualifiedName~NativeOwnedCollectionWholeEntity|FullyQualifiedName~StreamingEligibilityTests"

# Owned-collection count coverage, including the inverted test.
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCountTests"

# The REFERENCE-collection control the Task 1 probe could not cover (it needs a two-collection fixture).
# A $lookup always writes an array, so a projected/included reference collection should be untouched — this
# run is what actually establishes that, rather than the prediction.
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~CrossCollectionIncludeTests|FullyQualifiedName~QueryModeGateIncludeTests"
```

Expected: all green. `PrimitiveCollectionTests` green is the important one — those tests assert `null` for a nullable primitive list on missing and explicit-null BSON, and that is a *property serializer* path that this change must leave alone.

- [ ] **Step 10: Run the whole functional Query namespace**

A task filter scoped to one test class has already missed cross-class flips on this stack. Cast wider before committing:

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~FunctionalTests.Query"
```

Expected: zero failures. Any failure is either a genuine regression or another test encoding the old null — investigate and report rather than re-baselining silently.

- [ ] **Step 11: Prove the mutation pin has teeth**

Revert **only** the coalesce line from Step 4, rebuild, and re-run the new class:

```bash
git stash push src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~ProjectedCollectionNormalizationTests"
```

Expected: `Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection`,
`Bare_count_projection_returns_zero_for_a_missing_or_null_array` and
`Array_projection_normalizes_for_a_tracking_query` all go **RED**; the two controls stay green.

Record the observed failure messages. Then restore:

```bash
git stash pop
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

If any of the three stayed green, the pin is vacuous — say so and fix the test before continuing. "No test went red" is a finding about the test, not about the guard.

- [ ] **Step 12: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-358: projection path normalizes a missing or null embedded array to an empty collection"
```

**STOP.** Report to the user, including the Step 11 mutation result.

---

### Task 3: Breadth — collection type, nesting, and the whole-entity differential oracle

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs`

**Interfaces:**
- Consumes: from Task 2 — `Blog` / `Post` / `Comment`, `BlogModel`, `CreateContext(IMongoCollection<Blog>, MongoQueryMode)`, `UniqueCollectionName(string)`, `Row(string, BsonValue?)`, `PostsOf(params string[])`, `Seed(string)`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the collection-type breadth test**

The empty collection must be built by the navigation's own accessor, not hand-constructed as a `List<T>`. A non-`List` navigation is the cheapest proof. Add these nested types and the test to the class:

```csharp
    public class SetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public HashSet<Post> Posts { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> SetBlogModel = mb =>
        mb.Entity<SetBlog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    private static SingleEntityDbContext<SetBlog> CreateSetContext(
        IMongoCollection<SetBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: SetBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Non_list_collection_navigation_normalizes_through_its_own_accessor()
    {
        // The empty collection is built by PopulateCollection through the navigation's OWN
        // IClrCollectionAccessor, NOT hand-constructed. A HashSet<T> navigation is the cheapest proof of that:
        // an implementation that fabricated a List<T> for the empty case would throw InvalidCastException here
        // while every List<T>-based test above stayed green.
        var name = nameof(Non_list_collection_navigation_normalizes_through_its_own_accessor);
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany([
            Row("two", PostsOf("a", "b")),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        var collection = database.MongoDatabase
            .GetCollection<SetBlog>(raw.CollectionNamespace.CollectionName);

        using var db = CreateSetContext(collection, MongoQueryMode.Native);

        var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.All(rows, posts => Assert.IsType<HashSet<Post>>(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }
```

- [ ] **Step 2: Write the nested collection-in-collection test**

```csharp
    [Fact]
    public void Nested_collection_in_collection_normalizes_a_ragged_inner_array()
    {
        // The inner array is read from the PARENT ELEMENT document via BsonBinding.CreateGetBsonArray, a
        // different source than the bound _projectionBindings variable the outer array uses. Coalescing at the
        // point of use is what makes one line cover both; this test is what proves the second branch is covered.
        var name = nameof(Nested_collection_in_collection_normalizes_a_ragged_inner_array);
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));

        // One row, three posts: inner Comments present / absent / explicitly null.
        var posts = new BsonArray
        {
            new BsonDocument { { "Heading", "has" }, { "Comments", new BsonArray { new BsonDocument { { "Text", "c" } } } } },
            new BsonDocument { { "Heading", "absent" } },
            new BsonDocument { { "Heading", "null" }, { "Comments", BsonNull.Value } }
        };
        raw.InsertOne(Row("row", posts));
        var collection = database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var projected = db.Entities.AsNoTracking().Select(b => b.Posts).ToList().Single();
        var byHeading = projected.OrderBy(p => p.Heading).ToList();

        Assert.All(byHeading, p => Assert.NotNull(p.Comments));
        Assert.Equal(["absent", "has", "null"], byHeading.Select(p => p.Heading).ToArray());
        Assert.Equal([0, 1, 0], byHeading.Select(p => p.Comments.Count).ToArray());
    }
```

- [ ] **Step 3: Write the whole-entity differential oracle**

```csharp
    [Fact]
    public void Projected_collection_equals_the_whole_entity_oracle_for_every_array_state()
    {
        // THE DIFFERENTIAL GATE. Expected values come from materializing WHOLE ENTITIES and evaluating the same
        // selector client-side — the pattern owned-data slice 7 established for
        // Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state.
        //
        // Do NOT "simplify" the expected leg into a projection query: the whole-entity leg is precisely what
        // supplies the empty collections, and a projection query would be asserting the change against itself.
        //
        // Agreement between these two legs IS what EF-358 fixes, so this is the test most directly stating the
        // ticket, and the one to reach for first if a future change reopens the asymmetry.
        var collection = Seed(nameof(Projected_collection_equals_the_whole_entity_oracle_for_every_array_state));

        List<(string Title, int Count)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Select(b => (b.Title, b.Posts.Count)).OrderBy(r => r.Title).ToList();
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            List<(string Title, int Count)> actual;
            using (var db = CreateContext(collection, mode))
            {
                actual = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => new { b.Title, b.Posts }).ToList()
                    .Select(r => (r.Title, r.Posts.Count)).ToList();
            }

            Assert.Equal(expected, actual);
        }
    }
```

- [ ] **Step 4: Run the new tests**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~ProjectedCollectionNormalizationTests"
```

Expected: all eight PASS.

If `Non_list_collection_navigation_normalizes_through_its_own_accessor` fails on the `HashSet<Post>` mapping itself (rather than on the normalization), report it — a `HashSet<T>` owned collection may need explicit configuration in this provider, and if it genuinely cannot be mapped, substitute a different non-`List` `ICollection<T>` implementation and say which. Do **not** drop the breadth check.

If `Projected_collection_equals_the_whole_entity_oracle_for_every_array_state` fails only on the `DriverLinq` leg, check Task 1's Q1 finding — if the driver renders its own projection for this shape and never reaches the normalized path, scope that leg to `Native` and record why in the test comment.

If that same test fails because `Select(b => new { b.Title, b.Posts })` — an anonymous projection carrying an *entity collection* leaf, which routes through the mixed shaper — is not a supported shape at all, Task 1's "array leaf" probe row will already have said so. In that case substitute the bare `Select(b => b.Posts)` form for the actual leg (keeping the whole-entity expected leg exactly as written, since that is the oracle) and record in the test comment that the anonymous-array-leaf shape is unsupported independently of EF-358. Do **not** delete the oracle test.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs
git commit -m "EF-358: breadth - non-List navigation, nested inner arrays, whole-entity differential oracle"
```

**STOP.** Report to the user.

---

### Task 4: Documentation four-surface sweep, then the full verification sweeps

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: `docs/native-query-status-EF-322.md`
- Modify: `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md`
- Modify: `docs/superpowers/plans/2026-07-28-native-owned-collection-count-projections.md`

**Interfaces:**
- Consumes: Tasks 2 and 3 complete and green.
- Produces: nothing.

A correction is not done until it lands on **all four** surfaces. The status doc kept a refuted rationale alive through nine correction rounds because nobody diffed it, and it is the file read first on resume.

- [ ] **Step 1: Locate every surface mentioning the residual**

```bash
grep -rn "EF-358" --include="*.cs" --include="*.md" .
grep -rn "EF-357" --include="*.cs" --include="*.md" .
grep -rn "does NOT normalize\|materializes .null.\|ArgumentNullException" \
  src/MongoDB.EntityFrameworkCore/Query/AGENTS.md docs/native-query-status-EF-322.md \
  docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md \
  docs/superpowers/plans/2026-07-28-native-owned-collection-count-projections.md
```

Record the full hit list before editing. Every hit is either updated or consciously left alone with a reason.

- [ ] **Step 2: Rewrite the `Query/AGENTS.md` projection-path note**

Find the note beginning **"A GENERAL property of the projection path, not a count detail"**. It is now **false** and must be rewritten, not patched. The replacement must say:

- The projection path **now normalizes** a missing or explicitly-null embedded array to an empty collection, matching whole-entity materialization. Both states, alike.
- The mechanism: **two edits** — the null-collapse conditional deleted from `BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case, and a coalesce at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case. The empty collection is built by `PopulateCollection` through the navigation's own `IClrCollectionAccessor`, so a non-`List` navigation is correct for free.
- **The cross-visitor contract**, prominently, because it is the thing most likely to be re-broken: the injector's collection assignment must keep a `UnaryExpression` right-hand side, because `VisitBinary` hard-casts it. Folding the coalesce into that assignment throws `InvalidCastException` for every collection shaper in every query mode. An earlier design draft specified exactly that and was wrong.
- **EF-357 is now fully closed** (translation crash fixed in slice 7; materialization throw fixed here).
- **The parity claim, corrected precisely.** The old note said a `Native == DriverLinq` parity assertion "FAILS BY DESIGN" over a missing/null seed. For the **bare** count that is no longer true — both now answer 0, so parity is restored. For the **wrapped** count it is *still* true, but for an unrelated reason that this change does not touch: the driver renders a bare server-side `$size` with no `$ifNull` and aborts the aggregate with `MongoCommandException`. Keep those two cases distinct.
- **Primitive collections are unaffected and deliberately so** — a nullable primitive collection property still materializes as `null` for missing and explicit-null BSON (`PrimitiveCollectionTests`), because that is a property-serializer path with nullability-aware semantics, not a `CollectionShaperExpression`.
- **The `TypeAs` conflation**: absent and present-but-not-an-array are treated alike. Both were `null` before, so not a regression; recorded.
- Array-valued projections are still **not native** — that remains blocked on an alias-driven array read-back branch, since the DOM shaper's collection case hard-casts to `ObjectArrayProjectionExpression`.

- [ ] **Step 3: Update `docs/native-query-status-EF-322.md`**

Two places:

1. **§4** — the paragraph beginning "A general property of the projection path, not an owned-count detail — and the thing that limits EF-357's closure." Rewrite to record the fix, and remove the "first thing the array-projection follow-on will hit" framing since it no longer blocks. §5's array-projections bullet ("blocked on ... and, before that is observable-correct, on EF-358") must drop the EF-358 precondition.
2. **§6** — the EF-357 row becomes **closed** (both halves, naming this branch); the EF-358 row becomes **closed**. Update the closing sentence "Of these, EF-356 ... and EF-355 ... are the two that produce ... silent wrong data" only if it is affected — it is not, but confirm rather than assume.

- [ ] **Step 4: Update slice 7's design and plan docs — prose AND verbatim code blocks**

Amending prose alone has already carried a stale claim into a test comment two tasks later on this stack. Grep both files for code blocks as well as sentences:

```bash
grep -n "ArgumentNullException\|still_throws\|EF-358\|residual" \
  docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md \
  docs/superpowers/plans/2026-07-28-native-owned-collection-count-projections.md
```

Add a dated forward-reference note at each hit's section rather than rewriting history: these are as-built records of a completed slice, so the correct edit is "superseded by EF-358 (2026-07-29), which closed this residual" plus the corrected statement — not silent revision. In particular the plan's Task 3 code block containing `Bare_embedded_collection_Count_projection_still_throws_for_a_missing_or_null_array` must be annotated as superseded, since a future reader copying that block would reintroduce a now-false test.

- [ ] **Step 5: Verify no stale cross-references remain**

```bash
grep -rn "still_throws_for_a_missing_or_null_array" . ; echo "--- expected: only superseded-annotations in slice-7 docs ---"
grep -rn "PARTIALLY resolved\|partially resolved\|not closed\|Not closed" \
  --include="*.md" --include="*.cs" . | grep -i "357"
echo "--- expected: no output, or superseded-annotated hits only ---"
```

- [ ] **Step 6: Commit the docs**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md docs/
git commit -m "EF-358: four-surface doc sweep - projection path normalizes, EF-357 fully closed"
```

- [ ] **Step 7: Three-version sweep**

Invoke the `/test-all` skill (builds and tests EF8, EF9 and EF10 in parallel).

Expected: **zero failures on all three configurations**. Record, for each configuration, what was run and the outcome — and the *delta* against the pre-branch baseline, which should be uniform across the three (this branch adds no `#if`, so a non-uniform delta is itself a finding). Do not record a bare pass count as the result; three irreconcilable totals have come out of one branch's life on this stack.

- [ ] **Step 8: EF10 spec sweep, both axes**

Both axes, not just the pass set. The `All` slice was caught out by inventorying only `NativeOnly` pass/fail and missed a test that was `NativeOnly`-failing but whose `Native`-mode MQL baseline had moved.

```bash
# Axis 1 — default Native mode: catches any moved MQL baseline.
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build

# Axis 2 — NativeOnly: the "what actually goes native" report.
MONGODB_EF_NATIVE_ONLY=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: **zero delta on both axes**. Northwind has no owned collections, and this change alters no emitted MQL — it is a shaper-side change only — so any delta at all is a finding to investigate and report, not to re-baseline.

If a baseline genuinely needs regenerating, use `EF_TEST_REWRITE_BASELINES=1` with a tight `--filter`, then `git diff` the result and re-run **without** the variable to confirm it is genuinely green.

- [ ] **Step 9: Commit any sweep-driven changes and report**

```bash
git status --short
git add -A && git commit -m "EF-358: sweep results"   # only if the sweeps changed files
```

**STOP.** Report to the user: the three-version outcome, both spec axes, and the final doc hit list.

---

## Post-plan: whole-branch review

After Task 4, before any squash: run `/review-ef-core-provider` over the branch. On this stack the **final whole-branch review has caught the only Critical in three separate slices**, each time in an interaction between new code and pre-existing behaviour that no per-task review could see. Two things to hunt specifically here:

1. **Any other consumer of the deleted conditional's shape.** `MongoStreamingEntityMaterializerRewriter.FindCollectionShaper` is the one this plan knows about. Grep for other visitors that pattern-match `ConditionalExpression` around collection shapers.
2. **Silent wrong data from the `TypeAs` conflation** — specifically whether any real mapping can store a non-array where an array belongs and now silently reads as empty rather than failing.
