# Slice B — computed (non-field) sort keys

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let the native pipeline sort by an expression that is not a field path — `OrderBy(x => x.A + x.B)`,
`OrderBy(x => 1)`, `OrderBy(x => k)` — by emitting `$set` → `$sort` → `$unset` over a synthetic field, instead
of declining to driver-LINQ.

**Architecture:** MQL `$sort` accepts **field paths only**, so `MongoPipelineFactory.RenderSort` hard-throws for
any `MongoOrdering.KeySelector` that is not a `MongoFieldExpression`. The IR is already general
(`KeySelector` is typed `MongoExpression`), so the work is **IR + lowerer + renderer**, not a translator arm:
two new typed stages, three renderer arms, one rewrite in the lowerer's `MongoSortOp` arm, and a fall-through in
the slot populator. This is the one piece of EF-322 stream 1 that is not translator breadth at all.

**Tech Stack:** C# / EF Core provider (EF8/EF9/EF10 via build configurations), xUnit with plain `Assert.*`
(FluentAssertions is **not** referenced in the test projects), MongoDB C# driver, TestContainers
(`mongodb/mongodb-atlas-local`).

**Ticket:** **EF-401**. **Written from:** `docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md` — every
design decision below is that spike's, and its section numbers are cited so the reasoning is one hop away.
**Branch tip when this plan was written:** `ad7ed185`.

---

## Global Constraints

Copied from the merge plan's §8 and from the conventions this branch is held to. **Every task's requirements
implicitly include this section.**

- Rolling branch is **`NativeQueryOngoing`**. This slice goes on its own branch `EF-401`, is squashed to ONE
  commit, and is fast-forwarded onto the rolling branch. **Never force-push.** Keep `EF-401-presquash` until
  the work merges. **Do not push** — the owner pushes.
- Commit and PR titles start with a JIRA number: `EF-401: Description`.
- Full solution green on **EF8, EF9 and EF10**: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` and
  the EF8/EF9 equivalents, then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`.
- Both `MONGODB_URI` and `ATLAS_URI` **unset** — TestContainers boots a real `mongodb/mongodb-atlas-local`, so
  each `dotnet test` process gets its own container.
- **NEVER pipe `dotnet test` through `tail`/`head`** — it masks the exit code and truncates per-project
  summaries. Redirect to a file and read the file.
- **Launch every long `dotnet build`/`dotnet test` from a detached `nohup`'d script and poll its log.** On this
  host a run started via the sandbox's `run_in_background` **dies** if an unrelated foreground bash call times
  out and is auto-backgrounded. This cost the previous tranche two re-runs.
- **Rebuild before every measurement run**, including after reverting a mutation.
- **Zero `#if` lines added or removed in `.cs` under `src/`.** The tracked-file grep misses new files — check
  them directly.
- **Preserve each file's BOM state.** Files under `src/.../Query/NativeTranslation/` and its `Stages/`
  subdirectory have **no BOM**; check a sibling with `head -c 3 <file> | xxd -p` (`2f2a20` = no BOM,
  `efbbbf` = BOM) and match it.
- Every guard test **mutation-verified**. Record what you mutated and how many cases went red, in both
  directions. A test that stays green when you break the thing it names is not a test.
- **Assert VALUES and ORDER, never absence-of-throw and never a row count.** A dropped `$sort` returns the
  right number of rows in insertion order — a count assertion cannot see it.
- **Nativeness is proven only by a `MongoQueryMode.NativeOnly` run that succeeds**; a decline only by one that
  throws `NativeTranslationNotSupportedException`. MQL shape proves neither.
- Tag every documented claim **MEASURED / CITED / INFERRED / UNVERIFIED**.
- **Any prose count that also exists in a table must be re-summed from that table**, never restated.
- Breaking changes measured by **executing against the published packages** (`v10.0.2` / `v9.1.2` / `v8.4.2`),
  never inferred from the branch.
- Each subagent uses its **own uniquely-named scratchpad subdirectory** under
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/5114cfe3-e5e3-4b95-8967-8b81ee667ef9/scratchpad/`.
  Remove any worktree you create; `.claude/worktrees/agent-*` belong to other sessions — leave them alone.
- **Baseline (EF10) at `ad7ed185`, MEASURED:** default `Native` **4593 passed / 0 failed / 17 skipped**;
  `MONGODB_EF_NATIVE_ONLY=1` **2461 passed / 2132 failed / 17 skipped**.

### The one constraint that is specific to this slice, and it inverts the usual gate

**Slice B's acceptance gate is the MESSAGE-TRANSITION diff plus an `EF_TEST_REWRITE_BASELINES` pass — never
the raw pass count.** MEASURED by the spike (§4): with the prototype live, the `NativeOnly` counts were
**2461/2132/17 before and after, byte-identical**, while **exactly 12 cases moved** from
`NativeTranslationNotSupportedException: "Query is not natively representable"` to an `AssertMql` baseline
mismatch. Slice B re-bases the MQL of precisely the tests it converts, so its wins are invisible on the count
axis until the baselines are rewritten. **Judging this slice on the pass count would report 0 of 92 and could
plausibly get it cancelled.** This is the same both-axes trap `Query/AGENTS.md` already records for the
owned-collection `All` slice, reached from the opposite direction.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/.../Query/NativeTranslation/Stages/MongoAddFieldsStage.cs` | **new** — typed IR for `$set`, payload `IReadOnlyList<MongoProjection>` (alias → expression), modelled exactly on `MongoProjectStage`. BSON-free. |
| `src/.../Query/NativeTranslation/Stages/MongoUnsetStage.cs` | **new** — typed IR for `$unset`, payload `IReadOnlyList<string>`. BSON-free. |
| `src/.../Query/NativeTranslation/MongoPipelineFactory.cs` | two new `RenderStage` arms (`RenderAddFields`, `RenderUnset`); `RenderSort` widened to accept a `MongoElementRefExpression` key. |
| `src/.../Query/NativeTranslation/MongoSelectLowerer.cs` | the `MongoSortOp` arm of `AppendSelectOpStages` emits `$set`/`$sort`/`$unset`; the per-`Lower`-invocation synthetic-name allocator lives here. |
| `src/.../Query/NativeTranslation/NativeSlotPopulator.cs` | `OrderBy`/`ThenBy` arms fall through to `TryTranslateValue`, gated on `MongoAggregationExpressionRenderer.CanRender`. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs` | renderer arms + the widened `RenderSort`. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs` | the three-stage emission, the bare-`$sort` path, the allocator and the collision guard. |
| `tests/.../FunctionalTests/Query/NativeComputedSortTests.cs` | **new** — the five shaper shapes, the three `ThenBy`/mixed shapes, paging, tracking, and the declines. |
| `src/.../Query/AGENTS.md` | the as-built note. |
| `docs/native-query-status-EF-322.md` | §2 slice row. |

**Task boundaries.** Task 1 is renderable-in-isolation IR; Task 2 is the lowering; Task 3 makes it live and
proves it end to end; Task 4 is the spec sweep and the record. A reviewer can reject any one while accepting
its neighbours.

---

### Task 1: The two stages and their renderer arms

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoAddFieldsStage.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoUnsetStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` (the `RenderStage` switch, and `RenderSort`)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`

**Interfaces:**
- Consumes: `MongoProjection` (`internal readonly record struct MongoProjection(string Alias, MongoExpression Expression)`), `MongoElementRefExpression(string path, Type clrType)` with a `Path` property, `MongoAggregationExpressionRenderer.Render(MongoExpression, PlaceholderTable)`.
- Produces, for Task 2: `new MongoAddFieldsStage(IReadOnlyList<MongoProjection> fields)` exposing `Fields`; `new MongoUnsetStage(IReadOnlyList<string> fieldNames)` exposing `FieldNames`; and a `RenderSort` that accepts a `MongoElementRefExpression` `KeySelector` and renders it as its `Path`.

**Why a payload-bearing stage rather than reusing `MongoVectorSearchScoreStage`** (spike §2): that stage is a
payload-free MARKER whose whole point is that the factory renders it to a *fixed*
`{"$addFields": {"__score": {"$meta": "vectorSearchScore"}}}`, precisely so no BSON enters the lowerer. A
computed sort key needs a payload (name → expression). The right precedent is `MongoProjectStage`, which
already carries alias/expression pairs and renders through `MongoAggregationExpressionRenderer`.

- [ ] **Step 1: Create the slice branch**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout NativeQueryOngoing && git pull --ff-only
git checkout -b EF-401
head -c 3 src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoProjectStage.cs | xxd -p
```
Expect `2f2a20` — **no BOM**. Both new files must match.

- [ ] **Step 2: Write the failing renderer tests**

Add to `MongoPipelineFactoryTests` (follow the file's existing idiom — it builds stages directly and calls
`MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer())` then `Build(...)`, and asserts on the
resulting `BsonDocument[]`):

```csharp
[Fact]
public void AddFields_stage_renders_a_set_with_one_aggregation_expression_per_field()
{
    var sum = new MongoBinaryExpression(
        MongoBinaryOperator.Add,
        new MongoElementRefExpression("A", typeof(int)),
        new MongoElementRefExpression("B", typeof(int)));

    var stages = new MongoPipelineStage[]
    {
        new MongoAddFieldsStage([new MongoProjection("__sort0", sum)])
    };

    var pipeline = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer()).Build(new Dictionary<string, object?>());

    Assert.Single(pipeline);
    Assert.Equal(
        BsonDocument.Parse("""{ "$set" : { "__sort0" : { "$add" : ["$A", "$B"] } } }"""),
        pipeline[0]);
}

[Fact]
public void Unset_stage_renders_an_array_of_field_names()
{
    var stages = new MongoPipelineStage[] { new MongoUnsetStage(["__sort0", "__sort1"]) };

    var pipeline = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer()).Build(new Dictionary<string, object?>());

    Assert.Single(pipeline);
    Assert.Equal(BsonDocument.Parse("""{ "$unset" : ["__sort0", "__sort1"] }"""), pipeline[0]);
}

[Fact]
public void Sort_stage_accepts_an_element_ref_key_and_renders_it_as_a_plain_path()
{
    var stages = new MongoPipelineStage[]
    {
        new MongoSortStage([new MongoOrdering(new MongoElementRefExpression("__sort0", typeof(int)), Ascending: true)])
    };

    var pipeline = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer()).Build(new Dictionary<string, object?>());

    // "__sort0", NOT "$__sort0" — $sort takes field PATHS, not aggregation field references.
    Assert.Equal(BsonDocument.Parse("""{ "$sort" : { "__sort0" : 1 } }"""), pipeline[0]);
}

[Fact]
public void Sort_stage_still_rejects_a_key_that_is_neither_a_field_nor_an_element_ref()
{
    var stages = new MongoPipelineStage[]
    {
        new MongoSortStage([new MongoOrdering(new MongoConstantExpression(1, forSerialization: null), Ascending: true)])
    };

    // The lowerer is what turns a computed key into an element ref; a raw non-field key reaching the
    // renderer means that rewrite did not happen, and must stay loud rather than emit a wrong $sort.
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer()));
}
```

- [ ] **Step 3: Run them and confirm they fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoPipelineFactoryTests" > $SCRATCH/u.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/u.log
```
Expected: the first three fail (two do not compile until the stages exist — that IS the failure), the fourth
passes already (`RenderSort` throws for any non-`MongoFieldExpression` today).

- [ ] **Step 4: Create `MongoAddFieldsStage`**

New file, licence header copied byte-for-byte from `MongoProjectStage.cs`, **no BOM**:

```csharp
using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents a <c>$set</c> (<c>$addFields</c>) aggregation stage that adds one or more computed fields to
/// each document, leaving every existing field in place.
/// </summary>
/// <remarks>
/// EF-401 (stream 1, slice B). Emitted only as part of the <c>$set</c> → <c>$sort</c> → <c>$unset</c> triple
/// the lowerer produces for a COMPUTED sort key: MQL <c>$sort</c> accepts field paths only, so a non-field
/// key has to be materialized into a synthetic field first. The payload mirrors
/// <see cref="MongoProjectStage"/>'s — alias/expression pairs rendered through
/// <c>MongoAggregationExpressionRenderer</c> — which is what keeps the lowerer BSON-free.
/// </remarks>
internal sealed class MongoAddFieldsStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoAddFieldsStage"/> class.
    /// </summary>
    /// <param name="fields">The ordered fields to add, as alias/expression pairs.</param>
    public MongoAddFieldsStage(IReadOnlyList<MongoProjection> fields)
    {
        Fields = fields;
    }

    /// <summary>
    /// Gets the ordered fields to add, as alias/expression pairs.
    /// </summary>
    public IReadOnlyList<MongoProjection> Fields { get; }
}
```

- [ ] **Step 5: Create `MongoUnsetStage`**

Same header, **no BOM**:

```csharp
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents an <c>$unset</c> aggregation stage that removes one or more top-level fields from each document.
/// </summary>
/// <remarks>
/// EF-401 (stream 1, slice B). Removes the synthetic sort fields <see cref="MongoAddFieldsStage"/> added.
/// <para>
/// <b>Neither shaper needs this stage — it ships for SET-OP HYGIENE, and the comment is here so the next
/// reader does not measure that and delete it.</b> MEASURED (spike §3.2): with the <c>$unset</c> suppressed,
/// the streaming materializer, the DOM shaper, a trailing projection and a tracking round-trip all behave
/// identically, and no synthetic element is written back on <c>SaveChanges</c>. But the synthetic value
/// survives into the document STREAM, and two native operations downstream compare WHOLE documents —
/// <c>Union</c>'s dedup (<c>$group {_id: "$$ROOT"}</c>) and <c>Intersect</c>/<c>Except</c>'s source tagging
/// (<c>$group {_id: "$_doc"}</c>) — while a set-op operand is explicitly allowed to carry a sort
/// (<c>IsPlainWholeEntitySelect</c>). Without the <c>$unset</c> the synthetic value would fold into the
/// comparison key and change set semantics. One stage per query makes that structurally impossible.
/// </para>
/// </remarks>
internal sealed class MongoUnsetStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoUnsetStage"/> class.
    /// </summary>
    /// <param name="fieldNames">The top-level field names to remove.</param>
    public MongoUnsetStage(IReadOnlyList<string> fieldNames)
    {
        FieldNames = fieldNames;
    }

    /// <summary>
    /// Gets the top-level field names to remove.
    /// </summary>
    public IReadOnlyList<string> FieldNames { get; }
}
```

- [ ] **Step 6: Add the two renderer arms and widen `RenderSort`**

In `MongoPipelineFactory.RenderStage`'s switch, alongside `MongoProjectStage project => RenderProject(project, placeholders)`:

```csharp
            MongoAddFieldsStage addFields => RenderAddFields(addFields, placeholders),
            MongoUnsetStage unset => RenderUnset(unset),
```

Then, next to `RenderProject`:

```csharp
    // $set (a.k.a. $addFields) — adds computed fields, leaving existing ones alone. Unlike $project, a BARE
    // scalar here is a LITERAL, not an inclusion flag, which is what lets a constant sort key
    // (OrderBy(x => 1)) render as { "__sort0" : 1 } and mean it. The placeholder table is threaded through
    // because a sort key may be a query parameter (OrderBy(x => capturedLocal)).
    private static BsonDocument RenderAddFields(MongoAddFieldsStage stage, PlaceholderTable placeholders)
    {
        var body = new BsonDocument();
        foreach (var field in stage.Fields)
        {
            body.Add(field.Alias, MongoAggregationExpressionRenderer.Render(field.Expression, placeholders));
        }

        return new BsonDocument("$set", body);
    }

    private static BsonDocument RenderUnset(MongoUnsetStage stage)
        => new("$unset", new BsonArray(stage.FieldNames));
```

And in `RenderSort`, replace the `MongoFieldExpression`-only extraction with:

```csharp
        foreach (var ordering in stage.Orderings)
        {
            // A COMPUTED key arrives here already rewritten by the lowerer into a MongoElementRefExpression
            // naming the synthetic field its $set wrote (EF-401 slice B). $sort takes field PATHS, so both
            // arms contribute a bare path — never a "$"-prefixed aggregation field reference.
            var path = ordering.KeySelector switch
            {
                MongoFieldExpression field => field.ElementName,
                MongoElementRefExpression elementRef => elementRef.Path,
                _ => throw new NativeTranslationNotSupportedException(
                    $"$sort key selector must be a MongoFieldExpression or a MongoElementRefExpression; got "
                    + $"'{ordering.KeySelector.GetType().Name}'. A computed sort key should have been rewritten "
                    + "onto a synthetic field by MongoSelectLowerer.")
            };

            body.Add(path, ordering.Ascending ? BsonInt32.Create(1) : BsonInt32.Create(-1));
        }
```

- [ ] **Step 7: Run the unit tests and the three-EF-version build**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoPipelineFactoryTests" > $SCRATCH/u.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/u.log
for c in EF8 EF9 EF10; do dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" > $SCRATCH/b-$c.log 2>&1; echo "$c=$?"; done
```
Expected: all four new tests pass, the rest of the class unmoved, all three builds succeed.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "EF-401: add \$set/\$unset stages and widen RenderSort to an element-ref key"
```

---

### Task 2: Lower a computed sort key to `$set` → `$sort` → `$unset`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` (`AppendSelectOpStages`, plus a new private helper and allocator)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: Task 1's `MongoAddFieldsStage` / `MongoUnsetStage`.
- Produces, for Task 3: nothing new in signature — `Lower` still returns `IReadOnlyList<MongoPipelineStage>`. What Task 3 relies on is the *behaviour*: a `MongoSortOp` whose orderings are all `MongoFieldExpression` emits exactly one `MongoSortStage` as before, and one carrying any other key emits the three-stage triple.

**The emission site is decided** (spike §2, candidate (a)): the `MongoSortOp` arm of `AppendSelectOpStages`.
It is the only place where adjacency and ordering inside the verbatim arrival-order sequence are **structural**
rather than maintained — the three stages are produced together, at one site, and cannot be separated. It also
covers three call sites for free: the outer query's `PipelineOps`, a set-op operand's `PipelineOps`, and the
post-set-op `TrailingOps` all route through this one helper. **Candidate (b), recording extra ops in the
populator, is REJECTED**: `StartOrReplaceSort` replaces the tail op *if it is a `MongoSortOp`* and
`AppendThenBy` extends the tail sort, so bracketing ops would make both merge methods silently stop matching.

- [ ] **Step 1: Write the failing lowerer tests**

Add to `MongoSelectLowererTests`. It already has the helper you need: **`private static MongoQueryExpression
TestSelect()`**, which returns a fresh `MongoQueryExpression` over the file's test entity type — use it, do not
add a second one. Its existing tests call `new MongoSelectLowerer().Lower(select)` on the result.

You will also need two locals this file does not have: a `MongoFieldExpression` for a real mapped property of
that entity type (call it `FieldRef(query, "<name>")`, resolving the `IProperty` from
`query.CollectionExpression.EntityType` and passing its `GetElementName()`), and small `Sum()` / `Product()`
builders returning `MongoBinaryExpression`s over two `MongoElementRefExpression` operands. Define them as
private statics beside the existing helpers.

```csharp
[Fact]
public void Computed_sort_key_lowers_to_set_sort_unset()
{
    var query = TestSelect();
    var sum = new MongoBinaryExpression(
        MongoBinaryOperator.Add,
        new MongoElementRefExpression("A", typeof(int)),
        new MongoElementRefExpression("B", typeof(int)));
    query.Select.StartOrReplaceSort(new MongoOrdering(sum, Ascending: true));

    var stages = new MongoSelectLowerer().Lower(query);

    var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
    var sort = Assert.IsType<MongoSortStage>(stages[1]);
    var unset = Assert.IsType<MongoUnsetStage>(stages[2]);

    var synthetic = Assert.Single(addFields.Fields).Alias;
    Assert.Same(sum, Assert.Single(addFields.Fields).Expression);
    Assert.Equal(synthetic, Assert.IsType<MongoElementRefExpression>(Assert.Single(sort.Orderings).KeySelector).Path);
    Assert.Equal(synthetic, Assert.Single(unset.FieldNames));
    Assert.StartsWith("__sort", synthetic);
}

[Fact]
public void A_sort_with_only_field_keys_emits_a_bare_sort_and_no_set()
{
    // LOAD-BEARING, not tidiness. MEASURED (spike §6.2): a $set in front of a $sort disqualifies
    // index-backed sorting EVEN WHEN every sort key is a plain indexed field path — {$sort:{A:1}} is
    // IXSCAN A_1, and the identical sort preceded by an unrelated $set is a COLLSCAN.
    var query = TestSelect();
    query.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(query, "A"), Ascending: true));

    var stages = new MongoSelectLowerer().Lower(query);

    Assert.IsType<MongoSortStage>(Assert.Single(stages));
    Assert.DoesNotContain(stages, s => s is MongoAddFieldsStage or MongoUnsetStage);
}

[Fact]
public void A_mixed_sort_computes_only_the_computed_key_and_leaves_the_field_key_a_plain_path()
{
    var query = TestSelect();
    var product = new MongoBinaryExpression(
        MongoBinaryOperator.Multiply,
        new MongoElementRefExpression("A", typeof(int)),
        new MongoElementRefExpression("B", typeof(int)));
    query.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(query, "A"), Ascending: true));
    query.Select.AppendThenBy(new MongoOrdering(product, Ascending: false));

    var stages = new MongoSelectLowerer().Lower(query);

    var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
    var sort = Assert.IsType<MongoSortStage>(stages[1]);
    Assert.Single(addFields.Fields);                                   // only the computed key is materialized
    Assert.Equal(2, sort.Orderings.Count);
    Assert.IsType<MongoFieldExpression>(sort.Orderings[0].KeySelector);  // the field key stays a plain path
    Assert.IsType<MongoElementRefExpression>(sort.Orderings[1].KeySelector);
    Assert.False(sort.Orderings[1].Ascending);                          // direction is preserved per ordering
}

[Fact]
public void Two_computed_keys_in_one_sort_get_distinct_names_and_one_set_and_one_unset()
{
    var query = TestSelect();
    query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));
    query.Select.AppendThenBy(new MongoOrdering(Product(), Ascending: true));

    var stages = new MongoSelectLowerer().Lower(query);

    var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
    Assert.Equal(2, addFields.Fields.Count);
    Assert.Equal(2, addFields.Fields.Select(f => f.Alias).Distinct().Count());
    Assert.IsType<MongoSortStage>(stages[1]);
    Assert.Equal(2, Assert.IsType<MongoUnsetStage>(stages[2]).FieldNames.Count);
    Assert.Equal(3, stages.Count);   // ONE $set and ONE $unset per sort stage, not one per ordering
}

[Fact]
public void Synthetic_names_are_stable_across_repeated_lowering_of_the_same_query()
{
    // The prototype used a process-global counter and emitted __sort3 on one spec case and __sort4 on its
    // async twin (MEASURED, spike §2.1) — which would make every committed AssertMql baseline unstable.
    var first = new MongoSelectLowerer().Lower(BuildComputedSortQuery());
    var second = new MongoSelectLowerer().Lower(BuildComputedSortQuery());

    Assert.Equal(
        Assert.IsType<MongoAddFieldsStage>(first[0]).Fields[0].Alias,
        Assert.IsType<MongoAddFieldsStage>(second[0]).Fields[0].Alias);
}

[Fact]
public void A_synthetic_name_colliding_with_a_mapped_element_name_is_skipped()
{
    // $set OVERWRITES a same-named field silently — the same hazard IsWholeElementRepresentable's
    // sentinel-collision guard exists for on the owned bare-element path ($mergeObjects).
    var query = TestSelectWithReservedElementName();   // maps a property to element name "__sort0"

    query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

    var stages = new MongoSelectLowerer().Lower(query);

    Assert.NotEqual("__sort0", Assert.Single(Assert.IsType<MongoAddFieldsStage>(stages[0]).Fields).Alias);
}
```

Two helpers above are new and you write them beside the file's existing `TestSelect()`:
`BuildComputedSortQuery()` returns a fresh `TestSelect()` with one computed ordering already recorded, and
`TestSelectWithReservedElementName()` returns a query whose entity type maps a property with
`mb.Entity<T>().Property(x => x.Whatever).HasElementName("__sort0")`.

- [ ] **Step 2: Re-point the four pre-existing lowerer tests that use a constant as a sort key**

**Read this before running anything — four existing tests in this same file will go red, and the obvious fix
is the wrong one.** They use `new MongoConstantExpression(0, null)` as a *convenient placeholder* sort key,
not because they are about constant keys:

| test | line (approx) | asserts today | becomes |
|---|---:|---|---|
| `Predicate_ordering_offset_limit_lower_in_canonical_order` | 87 | `Assert.Equal(4, stages.Count)` | 6 stages |
| `Only_orderings_lower_to_single_sort_stage` | 137 | `Assert.Single(stages)` | 3 stages |
| `Sort_stage_carries_orderings_from_the_slot` | 184 | `Assert.Same(keyExpr, ordering.KeySelector)` | the key is rewritten to a `MongoElementRefExpression`, so `Assert.Same` fails |
| the trailing post-set-op sort test | 496 | a 3-element `Assert.Collection` | 5 elements |

**Do NOT bump the expected counts.** That would silently convert four tests about canonical stage ORDER,
ordering carry-through and set-op placement into tests about slice B, losing the coverage they were written
for. **Re-point each one at a genuine FIELD key** (`FieldRef(query, "<name>")`), which restores exactly the
behaviour they were asserting — a single `$sort` stage with the key carried through — and leaves slice B's own
new tests as the only place the three-stage triple is asserted.

Also check `NativeGroupByBinderTests.cs:128` and `MongoSelectDefinitionTrailingOpsTests.cs:58`, which likewise
record a constant sort key: if they do not call `Lower`, they are unaffected and must be left alone. Say in
your report which of the six you changed and why.

- [ ] **Step 3: Run them and confirm they fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1 && \
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectLowererTests" > $SCRATCH/u.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/u.log
```
Expected: `A_sort_with_only_field_keys_emits_a_bare_sort_and_no_set` passes already (that is today's
behaviour, and it must stay true); the other five fail.

- [ ] **Step 4: Add the synthetic-name allocator**

At the bottom of `MongoSelectLowerer`, as a private nested type:

```csharp
    // The synthetic field a computed sort key is materialized into. Double-underscore prefix, matching the
    // established sentinel convention — MongoVectorSearchScoreStage.ScoreField "__score",
    // MongoReplaceRootStage.OwnerKeyField "__ownerKey" / .OrdinalField "__ord".
    private const string SyntheticSortFieldPrefix = "__sort";

    /// <summary>
    /// Allocates the synthetic field names a computed sort key is materialized into, for ONE
    /// <see cref="Lower"/> invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per-invocation, deliberately — a process-global counter is a measured defect, not a style choice.</b>
    /// The slice-B prototype used one and emitted <c>__sort3</c> for a specification test and <c>__sort4</c>
    /// for its <c>async</c> twin, which would make every committed <c>AssertMql</c> baseline unstable across
    /// runs.
    /// </para>
    /// <para>
    /// <b>The reserved set is a collision guard, not decoration.</b> <c>$set</c> OVERWRITES a same-named
    /// existing field silently — the same hazard the owned bare-element path guards against for
    /// <c>$mergeObjects</c> (see <c>IsWholeElementRepresentable</c>'s sentinel-collision check). A model may
    /// map a property to any element name, including one of these, via <c>HasElementName</c>.
    /// </para>
    /// </remarks>
    private sealed class SyntheticSortFieldAllocator(IReadOnlyCollection<string> reservedElementNames)
    {
        private int _next;

        public string Allocate()
        {
            while (true)
            {
                var name = SyntheticSortFieldPrefix + _next++;
                if (!reservedElementNames.Contains(name))
                    return name;
            }
        }
    }

    // Top-level element names of the root entity type: every mapped property, plus the containing element
    // name of each owned navigation (an owned sub-document occupies a top-level element too).
    private static IReadOnlyCollection<string> TopLevelElementNames(IEntityType entityType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in entityType.GetProperties())
            names.Add(property.GetElementName());

        foreach (var navigation in entityType.GetNavigations())
        {
            if (navigation.IsEmbedded())
                names.Add(navigation.TargetEntityType.GetContainingElementName());
        }

        return names;
    }
```

**Known, accepted limitation to record in the as-built note (Task 4), tagged UNVERIFIED:** the reserved set is
built from the ROOT entity type. A set-op operand of a *different* entity type (only reachable via a projected
different-collection operand, EF-347 slice C1) is not covered, so a computed sort on such an operand over a
model that maps a property to `__sort0` would still collide. `MongoSetOperation` exposes only
`OperandSelect` and `OperandCollectionName`, not the operand's `IEntityType`, so covering it means widening
that IR — out of scope here.

- [ ] **Step 5: Emit the triple in the `MongoSortOp` arm**

Change `AppendSelectOpStages` to take the allocator and to route sorts through a new helper:

```csharp
    private static void AppendSelectOpStages(
        IReadOnlyList<MongoSelectOp> ops,
        List<MongoPipelineStage> stages,
        SyntheticSortFieldAllocator sortFields)
    {
        foreach (var op in ops)
        {
            if (op is MongoSortOp sortOp)
            {
                AppendSortStages(sortOp, stages, sortFields);
                continue;
            }

            stages.Add(op switch
            {
                MongoMatchOp m => new MongoMatchStage(m.Predicate),
                MongoSkipOp k => new MongoSkipStage(k.Count),
                MongoLimitOp l => new MongoLimitStage(l.Count),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unknown select op '{op.GetType().Name}'.")
            });
        }
    }

    /// <summary>
    /// Emits one <see cref="MongoSortOp"/>. A key that is already a field path is emitted as-is; a COMPUTED
    /// key is materialized into a synthetic field by a preceding <c>$set</c> and removed again by a following
    /// <c>$unset</c>, because MQL <c>$sort</c> accepts field paths only (EF-401, stream 1 slice B).
    /// </summary>
    /// <remarks>
    /// <b>ONE <c>$set</c> and ONE <c>$unset</c> per sort STAGE, carrying every computed key of that stage</b> —
    /// a <see cref="MongoSortOp"/> already holds a whole <c>OrderBy</c>/<c>ThenBy</c> chain's orderings as one
    /// op (EF-347), so the three stages bracket the whole sort.
    /// <para>
    /// <b>The no-computed-key early return is LOAD-BEARING, not tidiness.</b> MEASURED (spike §6.2) with a
    /// <c>queryPlanner</c> explain: <c>{$sort: {A: 1}}</c> over an indexed <c>A</c> is an <c>IXSCAN</c>, and
    /// the IDENTICAL sort preceded by an unrelated <c>$set</c> is a <c>COLLSCAN</c>. Emitting the <c>$set</c>
    /// unconditionally would silently cost every existing field sort its index. (It follows that a MIXED sort
    /// does not keep its field key index-usable either — that cost is accepted, since the alternative is not
    /// supporting computed sort keys at all; a <c>$match</c> ahead of the sort keeps its own index normally.)
    /// </para>
    /// </remarks>
    private static void AppendSortStages(
        MongoSortOp sortOp, List<MongoPipelineStage> stages, SyntheticSortFieldAllocator sortFields)
    {
        List<MongoProjection>? computed = null;
        var orderings = new List<MongoOrdering>(sortOp.Orderings.Count);

        foreach (var ordering in sortOp.Orderings)
        {
            if (ordering.KeySelector is MongoFieldExpression)
            {
                orderings.Add(ordering);
                continue;
            }

            var name = sortFields.Allocate();
            (computed ??= []).Add(new MongoProjection(name, ordering.KeySelector));
            orderings.Add(new MongoOrdering(
                new MongoElementRefExpression(name, ordering.KeySelector.Type), ordering.Ascending));
        }

        if (computed is null)
        {
            // Byte-identical to the pre-slice-B emission: the ORIGINAL ordering list, not the rebuilt one.
            stages.Add(new MongoSortStage(sortOp.Orderings));
            return;
        }

        stages.Add(new MongoAddFieldsStage(computed));
        stages.Add(new MongoSortStage(orderings));
        stages.Add(new MongoUnsetStage(computed.Select(f => f.Alias).ToList()));
    }
```

Then, at the top of `Lower`, construct the allocator once and thread it to all three
`AppendSelectOpStages` call sites (the outer `select.PipelineOps`, the set-op operand's
`setOp.OperandSelect.PipelineOps`, and the post-set-op `TrailingOps`):

```csharp
        var sortFields = new SyntheticSortFieldAllocator(
            TopLevelElementNames(query.CollectionExpression.EntityType));
```

- [ ] **Step 6: Run the lowerer tests**

Same command as Step 3. Expected: all six pass, the rest of the class unmoved.

- [ ] **Step 7: Mutation-verify the two guards**

1. **The no-computed-key early return.** Delete the `if (computed is null)` block so a `$set` is emitted
   unconditionally (with an empty field list). Rebuild, run the class, record the red count. Expect
   `A_sort_with_only_field_keys_emits_a_bare_sort_and_no_set` red. Revert, **rebuild**, re-run.
2. **The collision guard.** Change `SyntheticSortFieldAllocator.Allocate` to ignore `reservedElementNames`.
   Rebuild, run, record. Expect `A_synthetic_name_colliding_with_a_mapped_element_name_is_skipped` red.
   Revert, **rebuild**, re-run.

If either mutation turns nothing red, the test does not discriminate — fix the test before continuing.

- [ ] **Step 8: Confirm nothing else moved**

```bash
for c in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" > $SCRATCH/b-$c.log 2>&1
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $c" --no-build > $SCRATCH/t-$c.log 2>&1
  echo "$c=$?"; grep -E "Passed!|Failed!|Failed:" $SCRATCH/t-$c.log
done
```
Expected: **0 failures on all three.** Nothing is live yet — the populator still declines every computed key —
so any movement here is a regression in the field-key path.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "EF-401: lower a computed sort key to \$set/\$sort/\$unset"
```

---

### Task 3: Make it live, and prove it end to end

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (the `OrderBy`/`OrderByDescending` and `ThenBy`/`ThenByDescending` arms, plus one new private helper)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedSortTests.cs`

**Interfaces:**
- Consumes: Task 2's lowering.
- Produces: the behaviour Task 4 measures against the specification suite.

**Size, MEASURED (spike §4):** this makes **12 specification cases (6 tests × 2 async)** go native —
`OrderBy_client_Take`, `OrderBy_parameter`, `Skip_orderby_const`, `OrderBy_true`, `OrderBy_integer`
(all A3 bare constant/parameter) and `OrderBy_arithmetic`. **Those 12 are a RE-ATTRIBUTION inside the
stream-1 spike's already-counted 474, not 12 new cases — they must not be added to the ≈508 checkpoint.**

- [ ] **Step 1: Write the failing functional tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedSortTests.cs`. **Check a sibling
in the same directory for BOM state and match it** (`head -c 3 NativeBareProjectionTests.cs | xxd -p` —
`efbbbf` means BOM). Follow `NativeBareProjectionTests` for structure: `[XUnitCollection("QueryTests")]`,
`IClassFixture<TemporaryDatabaseFixture>`, and a private
`CreateContext(IMongoCollection<T> collection, MongoQueryMode mode)` built via `SingleEntityDbContext.Create`
with `new MongoDbContextOptionsBuilder(b).UseQueryMode(mode)` and
`b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))`.

**The fixture is the load-bearing part.** Seed four rows so that **sum order, `A` order, `B` order, insertion
order and label order are all mutually distinct** — otherwise a dropped `$sort` or a sort on the wrong key
passes. State the five orders in a comment and assert the expected order explicitly in every case.

Cases, each asserting **ORDER**, never a count:

1. `Computed_sort_over_a_whole_entity_streams` — `OrderBy(x => x.A + x.B)` under `NativeOnly`, whole entity.
   **Assert the streaming premise directly** — `Assert.True(StreamingEligibility.IsEligible(entityType))` —
   rather than assuming it; this is the shape the spike's central question was about, and the one-pass
   materializer's forward name-dispatch must `SkipValue()` the synthetic element.
2. `Computed_sort_over_a_DOM_entity` — the same sort over a TPH entity (a base with a derived sibling).
   **Assert both premises**: `GetDirectlyDerivedTypes().Any()` is true and `StreamingEligibility.IsEligible`
   is **false**, so this genuinely exercises the DOM shaper and not the streaming one.
3. `Computed_sort_then_projection` — `OrderBy(x => x.A + x.B).Select(x => new { x.Label, x.A })`; assert the
   order survives, the projected values are right, and **no synthetic field leaks into the result**.
4. `Computed_sort_then_paging` — `.Skip(1).Take(2)`; assert the **sum-ordered page**, which is a different
   pair from the insertion-ordered one.
5. `Computed_sort_tracking_round_trip` — default tracking; assert 4 tracked entries all `Unchanged`, that
   re-running returns the SAME instances (identity resolution), and that a mutate + `SaveChanges` round-trip
   **writes no `__sort*` element back** (read the raw `BsonDocument` to check).
6. `Mixed_sort_keeps_the_field_key_as_a_plain_path` — `OrderBy(x => x.A).ThenBy(x => x.A + x.B)` with
   deliberate ties on `A` so the secondary key is load-bearing; assert the full order.
7. `Computed_primary_then_field_secondary` — `OrderBy(x => x.A + x.B).ThenBy(x => x.Label)`, again with ties,
   asserting an order **different from** case 6 over the same four rows.
8. `Two_computed_keys` — `OrderBy(x => x.A + x.B).ThenByDescending(x => x.A * x.B)`.
9. `Constant_sort_key_goes_native` — `OrderBy(x => 1)`; this is the A3 shape that delivers 10 of the 12.
   A constant is a LITERAL in `$set` (unlike `$project`, where it would be an inclusion flag).
10. `Parameterized_sort_key_goes_native` — `OrderBy(x => capturedLocal)`, proving the placeholder table is
    threaded through `RenderAddFields`.
11. `Field_sort_emits_no_set` — capture the MQL for a plain `OrderBy(x => x.A)` and assert it contains no
    `$set`. **Caption it as a STAGE-SHAPE pin, not a routing proof.**
12. `Unsupported_computed_key_declines_and_returns_correct_rows` — `OrderBy(x => x.Label.ToUpper())` (a method
    call the translator does not support): correct rows under `Native`, `NativeTranslationNotSupportedException`
    under `NativeOnly`.
13. `Parameterized_where_leg` — the mandatory late-decline leg: a captured local inside `string.StartsWith`
    alongside a computed sort, run under the **default `Native`** mode, asserting values and order.

- [ ] **Step 2: Run them and confirm they fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1 && \
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeComputedSortTests" > $SCRATCH/f.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/f.log
```
Expected: cases 11 and 12 pass already (they assert today's behaviour); every computed-sort case fails under
`NativeOnly` with `NativeTranslationNotSupportedException`.

- [ ] **Step 3: Add the populator fall-through**

In `NativeSlotPopulator`, change the two sort arms to try a computed key before giving up:

```csharp
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // Same as Where above (EF-347 Task 2): a $sort recorded after paging is emitted after it,
            // verbatim — correct by sequential pipeline semantics. No canonical-order guard.
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
            // Same as OrderBy above (EF-347 Task 2): no canonical-order guard.
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

and add the shared helper:

```csharp
    /// <summary>
    /// Attempts to translate a COMPUTED (non-field) sort key — EF-401, stream 1 slice B. MQL <c>$sort</c>
    /// accepts field paths only, so <see cref="MongoSelectLowerer"/> materializes the result into a synthetic
    /// field with <c>$set</c> and removes it again with <c>$unset</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate is <see cref="MongoAggregationExpressionRenderer.CanRender"/>, NOT
    /// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>, and the difference is the whole point.</b>
    /// A <c>$set</c> body is an AGGREGATION expression, so a node kind that exists only in the query dialect
    /// can serve a predicate and can NEVER serve a computed sort key. Gating here turns that into a clean
    /// translate-time decline instead of a render-time throw.
    /// </para>
    /// <para>
    /// <b>Consequence for future capability-A slices, which no other document states:</b> a slice whose sort
    /// column is to count must add an arm to <see cref="MongoAggregationExpressionRenderer"/>
    /// (<c>Render</c> AND <c>CanRender</c>, which that file's own contract requires be changed together) —
    /// not only to <c>MongoQueryLanguageRenderer</c>/<c>IsQueryDialectRenderable</c>. `CanRender` today admits
    /// field/element refs, constants/parameters, binary operators over its 13 listed operators and the two
    /// size nodes — <b>not</b> <c>MongoInExpression</c>, <c>MongoRegexExpression</c>,
    /// <c>MongoElemMatchExpression</c> or <c>MongoUnaryExpression</c>. The stream-1 spike's §7 imposes this
    /// only on slices introducing a NEW node kind, so A6 (<c>Contains</c>) and A13 (<c>Not</c>) — whose node
    /// kinds already exist — fall outside it and would otherwise ship with their sort columns silently dead.
    /// </para>
    /// <para>
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> brings its own two guards with it: an
    /// integer-result division is rejected (MongoDB's <c>$divide</c> is non-truncating), and so is an operand
    /// whose property lacks default serialization — so a value-converted field cannot reach a computed sort
    /// key and be sorted by its RAW stored order. (A plain FIELD sort key on such a property has no equivalent
    /// guard and is unchanged by this slice — pre-existing, not introduced here.)
    /// </para>
    /// </remarks>
    private static bool TryTranslateComputedSortKey(
        MongoExpressionTranslator translator,
        Expression keySelectorBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!translator.TryTranslateValue(keySelectorBody, out var translated))
            return false;

        if (!MongoAggregationExpressionRenderer.CanRender(translated))
            return false;

        result = translated;
        return true;
    }
```

- [ ] **Step 4: Run the functional tests**

Same command as Step 2. Expected: all 13 pass.

- [ ] **Step 5: Mutation-verify that the net has teeth**

1. **Disable the fall-through** — make `TryTranslateComputedSortKey` return `false` unconditionally. Rebuild,
   run the class, record the red count. Every computed-sort case must go red. Revert, **rebuild**, re-run.
2. **Break the sort, keep the rows** — make `AppendSortStages` emit the `$set` and `$unset` but **skip the
   `$sort`**. Rebuild, run, record. This is the mutation that proves the assertions pin ORDER and not counts;
   if fewer cases go red than in mutation 1, the difference names the cases whose assertions are too weak —
   fix them. Revert, **rebuild**, re-run.

- [ ] **Step 6: Run the whole solution on all three EF versions**

```bash
for c in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" > $SCRATCH/b-$c.log 2>&1
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $c" --no-build > $SCRATCH/t-$c.log 2>&1
  echo "$c=$?"; grep -E "Passed!|Failed!|Failed:" $SCRATCH/t-$c.log
done
```

Expected: 0 failures on EF8 and EF9. **On EF10 the specification project is expected to report 12 failures** —
the `AssertMql` re-bases Task 4 handles. Confirm they are exactly the 6 tests named above (× 2 async) and that
**every one is an `AssertMql` string mismatch, not a data assertion** (`AssertMql` runs after the base EF Core
result assertion, so a data failure would be a different and much more serious thing). If any other test
fails, or any failure is a data assertion, stop and report.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "EF-401: translate a computed sort key, gated on the aggregation renderer"
```

---

### Task 4: Spec sweep, re-baseline, and the record

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindAggregateOperatorsQueryMongoTest.cs`, `.../NorthwindMiscellaneousQueryMongoTest.cs` (the 12 `AssertMql` re-bases)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (as-built note)
- Modify: `docs/native-query-status-EF-322.md` (§2 slice row)

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: the merged slice.

- [ ] **Step 1: Capture the before-and-after message transition**

The gate for this slice is the transition, not the count (see the slice-specific Global Constraint). Run the
`NativeOnly` axis at this tip and compare against the branch baseline:

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=sliceb-before-rebase.trx" --results-directory $SCRATCH \
  > $SCRATCH/spec-no.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-no.log
```

Expected, and this is the counter-intuitive part: **2461 / 2132 / 17 — byte-identical to the baseline**, with
**12 cases whose failure MESSAGE changed** from
`NativeTranslationNotSupportedException : Query is not natively representable` to an `Assert.Equal` string
mismatch naming `$set`. Extract the 12 by comparing `(testName → outcome, first 120 chars of message)` against
a baseline TRX. **If the transition set is not exactly the 6 tests × 2 async cases the spike measured, that is
a finding to report with the names — investigate before re-baselining.**

- [ ] **Step 2: Re-baseline the 12**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/rebase.log 2>&1
git diff --stat tests/MongoDB.EntityFrameworkCore.SpecificationTests/
```

Then **read every changed baseline** and confirm each new pipeline is what this slice should emit — a `$set`
naming a deterministic `__sortN`, a `$sort` on that name, and an `$unset` removing it. The expected bodies,
MEASURED by the spike (§4), are `{"__sortN": 42}`, `{"__sortN": 5}`, `{"__sortN": true}` (×2),
`{"__sortN": 3}` and `{"__sortN": {"$subtract": …}}`. **Confirm the synthetic name is identical across each
test's two `async` cases** — if it is not, Task 2's allocator is not per-invocation and the baselines are
unstable; stop and fix that rather than committing unstable baselines.

- [ ] **Step 3: Re-run both axes and confirm the landing**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/spec-native.log 2>&1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=sliceb-after.trx" --results-directory $SCRATCH \
  > $SCRATCH/spec-no2.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-native.log $SCRATCH/spec-no2.log
```

Expected: default `Native` back to **4593 / 0 / 17**; `NativeOnly` **2473 / 2120 / 17** — that is
`2461 + 12` passed and `2132 − 12` failed. **Re-derive those two numbers from the baseline plus the measured
transition count rather than trusting this line**, and report `Failed→Passed` and `Passed→Failed` as sets.
`Passed→Failed` must be empty.

- [ ] **Step 4: Re-run the whole solution on all three EF versions**

As Task 3 Step 6. Expected now: **0 failures on all three.**

- [ ] **Step 5: Check the break rubric against the release tags**

Every type this slice touches is `internal`; `MongoQueryMode` does not exist at `v10.0.2` / `v9.1.2` /
`v8.4.2`, so a released package ran every one of these queries through driver-LINQ. The change is
fallback → native with **unchanged results**, plus changed emitted MQL for supported queries — both explicitly
carved out by the rubric at the top of `Query/AGENTS.md`. **Confirm rather than assume**, and state in the
report which of "no entry needed" or "entry added" you concluded and on what evidence. The one thing worth
probing directly: a query whose sort key is computed returned rows in some order at the published version
(via driver-LINQ) and returns them in that same order now — order is the observable here, not just values.

- [ ] **Step 6: Write the AGENTS.md as-built note**

Add a note to `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` in the established style. It must carry:

- what is now native (a computed sort key in `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`), and
  the emitted shape `$set` → `$sort` → `$unset`;
- **the MEASURED index cost** (spike §6.2): a computed sort is a COLLSCAN, and a `$set` in front of a `$sort`
  disqualifies index-backed sorting **even for a plain field key** — which is why the lowerer must not emit a
  `$set` when nothing is computed, and why a MIXED sort loses its field key's index. Record that a `$match`
  ahead of the sort keeps its own index. This is the same correctness-over-index trade the owned-collection
  `All` slice took and documented;
- **why the `$unset` ships even though neither shaper needs it** (set-op `$$ROOT` hygiene, spike §3.2/§3.3) —
  stated so the next reader who measures that no shaper needs it does not delete it;
- **the `CanRender` obligation every future capability-A slice inherits** — at least A6 (18) + A13 (18) = 36
  of the 92 sort-position cases need an aggregation-dialect arm and are NOT covered by the stream-1 spike's
  new-node-kind note, because `MongoInExpression` and `MongoUnaryExpression` already exist;
- the measured yield: **12 cases (6 tests × 2 async)**, and that these are a **RE-ATTRIBUTION** inside the
  stream-1 spike's 474 — **≈508 does not move, and A3's marginal yield once slice B has shipped is 30, not
  40**;
- **the acceptance-gate trap**: the `NativeOnly` pass count was byte-identical before and after, because the
  slice re-bases the baselines of exactly the tests it converts;
- the synthetic-name convention (`__sortN`, per-`Lower`-invocation counter, collision guard) and the
  **UNVERIFIED** residual that the reserved set covers only the root entity type;
- the mutation evidence from Tasks 2 and 3.

Tag every number MEASURED / CITED / INFERRED / UNVERIFIED, and re-sum every count from the table beside it.

- [ ] **Step 7: Add the slice row to the status doc**

One row in `docs/native-query-status-EF-322.md` §2, matching the existing columns: the slice, EF-401, the
squashed commit SHA, and the outcome (12 `NativeOnly` wins, 12 `AssertMql` re-bases, 0 regressions,
`BREAKING-CHANGES.md` disposition). **Do not add the 12 to the ≈508 checkpoint figure** — say in the row that
it is a re-attribution, and state A3's corrected marginal yield of 30.

- [ ] **Step 8: Squash, fast-forward — do NOT push**

```bash
git branch -f EF-401-presquash HEAD
git reset --soft $(git merge-base HEAD NativeQueryOngoing)
git commit -F $SCRATCH/msg.txt
git diff --quiet EF-401-presquash HEAD && echo "squash content-identical"
git checkout NativeQueryOngoing && git merge --ff-only EF-401
```

`msg.txt` starts `EF-401: sort by a computed key via $set/$sort/$unset (stream 1, slice B)` and records: the
emitted shape and why `$sort` needs it; the emission site and why the populator alternative was rejected; the
index measurement; the `$unset`'s set-op justification; the 12 wins and their re-attribution status; the 12
re-baselines; the `CanRender` obligation for future A slices; and the three-EF-version and both-axis results.

---

## What comes after this plan

The rest of stream 1's capability-A slices. The measured slice-B-independent tranche was
**A1, A2, A4, A5 = 158** (spike §5.1); A2 and A5 shipped in the previous tranche, leaving **A1** (casts, 56
sole-cause, of which 6 need slice B — its narrowing guard must **not** simply be relaxed) and **A4** (the
reverted tier 2, 28 sole-cause, which has a recorded prerequisite: the late-fallback path must emit `$ifNull`
itself rather than inherit the driver's bare `$size` — see the step-3a note in `Query/AGENTS.md`).

**Three things this slice changes about how the rest of stream 1 should be planned:**

1. **Any A slice whose sort column is to count must add an aggregation-dialect renderer arm** — at least
   A6 (18) and A13 (18) are affected and no existing document says so.
2. **One pre-existing question A1 must settle, surfaced by this spike and explicitly NOT a slice-B concern**
   (spike §8): `TryTranslateField` calls `Unwrap`, which strips **any** `Convert`/`ConvertChecked`
   unconditionally with no widening/narrowing check. So `OrderBy(x => (int)x.D)` over a `double` field is
   already native today and sorts by the **raw double**, not the truncated value. UNVERIFIED whether a fixture
   that discriminates them (e.g. 1.4 and 1.6, which tie at `(int)` but not raw) diverges from in-memory LINQ or
   from driver-LINQ. Behaviour is identical with slice B disabled, so it is not this slice's — but A1's whole
   subject is which `Convert`s may be unwrapped, so A1 should settle it.

3. **A slice's sort-position yield cannot be read off its case count.** MEASURED (spike §5.1.1, over 692
   declining sort-key occurrences): all 18 `Not` sort keys are `Not(<param>.Contains(…))`, needing A13 *and*
   A6 *and* slice B; 50 of 52 `?:` keys carry a method-call test, a transparent identifier or a `Convert` in
   the test. `??` is the one large group measured clean (38 of 38 over supported operands). This is the same
   family of over-count that made A5 convert 0 of 36 — **conversion of the ≈80 remains UNVERIFIED.**
