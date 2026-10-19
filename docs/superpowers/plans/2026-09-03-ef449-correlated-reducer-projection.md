# EF-449: Correlated Reducer Projection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `nav.FirstOrDefault()/First()[.OrderBy(...)][(predicate)].Member` — a reference-collection
navigation reduced to one element and read as a scalar, inside a projection leaf — natively translate to
a `$lookup`(+sub-pipeline)+`$unwind`, closing EF-449 / one concrete case of EF-216
(`BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default`).

**Architecture:** Reuse the existing reference-Include/Join `$lookup`+`$unwind` primitive
(`LookupExpression`, `MongoSelectLowerer.AppendLookupStages`, `MongoPipelineFactory.RenderLookup`), not
`NativeSelectManyBinder`. A new `NativeProjectionBinder` recognizer detects the shape and builds a
`LookupExpression` carrying a `$sort`/`$match`/`$limit:1` sub-pipeline, tagged with a new, narrow
`LookupPipelineKind.CorrelatedReducer` discriminator so none of the *existing* `HasPipeline` consumers
(TPH discriminator narrowing, nested Include lookups — both fallback-only today) are affected. This
family has **no driver-LINQ oracle** (confirmed: the shape already hard-fails in every `MongoQueryMode`
today, inside the fallback bridge itself), so an unsupported variant simply keeps failing exactly as
today — no new decline/fallback plumbing.

**Tech Stack:** C#/.NET, MongoDB aggregation pipeline (`$lookup`, `$unwind`, `$sort`, `$match`, `$limit`),
xUnit.

**Spec:** `docs/superpowers/specs/2026-09-03-ef449-correlated-reducer-projection-design.md`

## Global Constraints

- Every source change must build under `Debug EF8`, `Debug EF9`, and `Debug EF10` (`dotnet build
  MongoDB.EFCoreProvider.sln -c "Debug EF10"` etc.) — no `#if` is expected for this feature (pure
  aggregation-pipeline generation), but each task's "run tests" step should be run against EF10 during
  development and the full `/test-all` sweep run once at the end (Task 9).
- **Scope-narrowing discovered during planning, not in the original spec:** `LookupExpression.PipelineStages`
  is a `List<BsonDocument>` with **no placeholder/parameter-substitution mechanism** — every existing
  producer of `PipelineStages` (TPH discriminator `$match`, filtered-Include `$skip`/`$limit`) only ever
  embeds **constants**, never a query parameter. `MongoPipelineFactory`'s placeholder substitution
  (`PlaceholderTable`, `Build(parameterValues)`) only reaches stages built through the *ordinary*
  `MongoSelectDefinition.PipelineOps`/`MongoExpression` path, not raw `BsonDocument`s sitting in
  `PipelineStages`. **Consequence: v1's optional `FirstOrDefault(predicate)`/`First(predicate)` predicate
  must be restricted to a predicate whose translated value(s) are compile-time constants** (matching the
  existing filtered-Include precedent's `ConstantExpression` handling for `Skip`/`Take` — see
  `MongoProjectionBindingExpressionVisitor.Lookup.cs:700-711`). A predicate referencing a query parameter
  or captured closure variable **declines this leaf** (falls through to the pre-existing "Unsupported
  cross-DbSet query" exception — no regression, just doesn't yet go native). This is a scope narrowing
  from the design spec's §"full shape matrix" framing; call it out explicitly if/when the spec is revised.
- Naming: follow existing convention — `Mongo*`/`Native*` prefixes, `internal` visibility unless the
  design spec says otherwise.
- Per this repo's stacked-PR convention, all commits in this plan land on the local branch
  `native-EF-322-Native-LINQ-rebased` (or wherever `NativeQueryOngoing` currently points — confirm with
  `git branch --show-current` at Task 1 start) and get **squashed into one commit onto
  `NativeQueryOngoing`** at the end (Task 9's last step), not merged commit-by-commit. Keep normal
  per-task commits during development; squash only at the end.
- Per this repo's subagent-driven-development convention, **stop after every task** for review by
  default (this is the `subagent-driven-development` skill's own behavior — no extra action needed here
  beyond following that skill).

---

### Task 1: `LookupExpression.LookupPipelineKind` discriminator

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/LookupExpressionTests.cs` (create if
  it doesn't exist — check first with `find tests -iname "LookupExpressionTests.cs"`)

**Interfaces:**
- Produces: `internal enum LookupPipelineKind { None, FallbackOnly, CorrelatedReducer }` and
  `public LookupPipelineKind PipelineKind { get; private init; }` on `LookupExpression`, defaulting to
  `None`. Every existing `PipelineStages.Add(...)` call site outside this ticket's new code must leave
  the lookup at `FallbackOnly` once it has added anything.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/LookupExpressionTests.cs
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class LookupExpressionTests
{
    [Fact]
    public void New_lookup_defaults_to_PipelineKind_None()
    {
        var lookup = new LookupExpression(SampleNavigations.CustomerOrders);

        Assert.Equal(LookupPipelineKind.None, lookup.PipelineKind);
    }
}
```

(If `SampleNavigations` — or an equivalent shared test-model helper providing a real `INavigation` — does
not already exist in this test project, find the nearest existing unit test that constructs a
`LookupExpression` today, e.g. `grep -rn "new LookupExpression(" tests/MongoDB.EntityFrameworkCore.UnitTests/`,
and reuse whatever navigation/model fixture it uses instead of inventing a new one.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~LookupExpressionTests"`
Expected: FAIL — `LookupPipelineKind` does not exist / `PipelineKind` member not found (compile error).

- [ ] **Step 3: Add the discriminator and wire the existing fallback-only sites**

In `LookupExpression.cs`, add near `PipelineStages`/`HasPipeline`:

```csharp
/// <summary>
/// Distinguishes WHY this lookup carries a non-empty <see cref="PipelineStages"/> sub-pipeline, since
/// each reason has a different native-eligibility answer. <see cref="None"/>: no pipeline stages.
/// <see cref="FallbackOnly"/>: TPH discriminator narrowing or a nested Include lookup — both remain
/// fallback/mixed-visitor-only (see <see cref="MongoSelectLowerer.AppendLookupStages"/>'s exhaustive
/// pipeline-kind dispatch). <see cref="CorrelatedReducer"/>: a reference-collection-nav
/// First/FirstOrDefault projection leaf (EF-449) — the one kind the NATIVE lowerer knows how to emit.
/// </summary>
internal enum LookupPipelineKind
{
    None,
    FallbackOnly,
    CorrelatedReducer
}

/// <summary>See <see cref="LookupPipelineKind"/>.</summary>
public LookupPipelineKind PipelineKind { get; internal set; } = LookupPipelineKind.None;
```

In the constructor's TPH-narrowing block (the existing `if (targetEntityType.FindDiscriminatorProperty()
is { } discriminatorProperty && ...)` block that adds the discriminator `$match`), add
`PipelineKind = LookupPipelineKind.FallbackOnly;` as the last line inside that `if`.

In `MongoProjectionBindingExpressionVisitor.Lookup.cs`, every method that pushes onto
`somelookup.PipelineStages` for an Include/nested-lookup purpose must mark that lookup `FallbackOnly`
immediately after. Concretely (grep `PipelineStages.Add\|PipelineStages.AddRange` in that file to confirm
you've got all of them — there are five in the file as of this writing):
- `ExtractNestedIncludePipeline` (`parentLookup.PipelineStages.Add(BuildLookupDocument(nestedLookup));`) →
  add `parentLookup.PipelineKind = LookupPipelineKind.FallbackOnly;` right after.
- `ExtractThenIncludesFromSubquery` (two `PipelineStages.Add` call sites in that method) → same, on
  `parentLookup`.
- `AddReferenceLookupStages` (two `PipelineStages.Add` calls, on `parentLookup`) → same.
- `ExtractFilteredIncludePipeline`'s final `lookup.PipelineStages.AddRange(stages);` → guard it:
  ```csharp
  if (stages.Count > 0)
  {
      lookup.PipelineStages.AddRange(stages);
      lookup.PipelineKind = LookupPipelineKind.FallbackOnly;
  }
  ```

- [ ] **Step 4: Run test to verify it passes**

Run the same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Regression-check the fallback sites are still marked correctly**

Add one more unit test alongside the first, using whatever fixture in this test project already exercises
TPH discriminator narrowing (search `grep -rln "FindDiscriminatorProperty\|GetDerivedTypes" tests/MongoDB.EntityFrameworkCore.UnitTests/Query/`
for an existing TPH `LookupExpression` test to model this on) asserting the constructed lookup's
`PipelineKind == LookupPipelineKind.FallbackOnly` when a discriminator-narrowed navigation is passed in.

- [ ] **Step 6: Run full unit test project, then commit**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"`
Expected: all PASS (no regressions).

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/LookupExpressionTests.cs
git commit -m "EF-449: add LookupExpression.PipelineKind discriminator"
```

---

### Task 2: `MongoPipelineFactory.RenderLookup` emits `pipeline` when present

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs:417-424`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`
  (check `find tests -iname "MongoPipelineFactoryTests.cs"` first; if it doesn't exist, find whatever unit
  test currently exercises `RenderLookup`/`MongoLookupStage` rendering — `grep -rln "MongoLookupStage" tests/MongoDB.EntityFrameworkCore.UnitTests/` — and add to that file instead)

**Interfaces:**
- Consumes: `LookupExpression.HasPipeline`, `LookupExpression.PipelineStages` (Task 1, pre-existing).
- Produces: `RenderLookup` now includes a `"pipeline"` array field (each entry a stage from
  `PipelineStages`, in order) whenever `HasPipeline` is true, alongside the existing
  `from`/`localField`/`foreignField`/`as`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RenderLookup_includes_pipeline_field_when_HasPipeline()
{
    var lookup = new LookupExpression(SampleNavigations.CustomerOrders); // or your project's existing fixture
    lookup.PipelineStages.Add(new BsonDocument("$limit", 1));

    var rendered = MongoPipelineFactoryTestHelpers.RenderLookupForTest(lookup); // see Step 3 note

    var lookupDoc = rendered["$lookup"].AsBsonDocument;
    Assert.True(lookupDoc.Contains("pipeline"));
    Assert.Equal(new BsonArray { new BsonDocument("$limit", 1) }, lookupDoc["pipeline"]);
    Assert.Equal("from_customers", lookupDoc["from"].AsString); // adjust to the fixture's actual From value
}

[Fact]
public void RenderLookup_omits_pipeline_field_when_no_pipeline()
{
    var lookup = new LookupExpression(SampleNavigations.CustomerOrders);

    var rendered = MongoPipelineFactoryTestHelpers.RenderLookupForTest(lookup);

    Assert.False(rendered["$lookup"].AsBsonDocument.Contains("pipeline"));
}
```

`RenderLookup` is `private static` on `MongoPipelineFactory`. Check whether this test project already has
an `InternalsVisibleTo`-based way to reach private static members for testing this class (search
`grep -rn "RenderLookup\|MongoLookupStage" tests/MongoDB.EntityFrameworkCore.UnitTests/`). If the existing
pattern is to test lookup rendering indirectly through `MongoPipelineFactory.Create([...MongoLookupStage(lookup)], ...)`'s
public entry point instead of calling `RenderLookup` directly, use that pattern instead of inventing a new
test-only accessor — match whatever the existing `MongoLookupStage` rendering tests already do.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~RenderLookup"`
Expected: FAIL — no `pipeline` field present.

- [ ] **Step 3: Implement**

Replace the existing `RenderLookup` body (`MongoPipelineFactory.cs:417-424`):

```csharp
private static BsonDocument RenderLookup(LookupExpression lookup)
{
    var lookupDoc = new BsonDocument
    {
        { "from", lookup.From },
        { "localField", lookup.LocalField },
        { "foreignField", lookup.ForeignField }
    };

    if (lookup.HasPipeline)
    {
        lookupDoc.Add("pipeline", new BsonArray(lookup.PipelineStages));
    }

    lookupDoc.Add("as", lookup.As);
    return new BsonDocument("$lookup", lookupDoc);
}
```

(MongoDB's `$lookup` supports `localField`/`foreignField` combined with an additional `pipeline` — the
pipeline runs over the already-equi-joined subset. This is additive: today no `HasPipeline` lookup ever
reaches this method, because `AppendLookupStages` throws first — see Task 1's note and Task 3 below — so
this change is inert until Task 3 lands.)

- [ ] **Step 4: Run test to verify it passes**

Same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Run full unit test project, then commit**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs
git commit -m "EF-449: render \$lookup pipeline field when present"
```

---

### Task 3: `MongoSelectLowerer.AppendLookupStages` — new `CorrelatedReducer` branch

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:342-404`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`
  (find the existing file with `find tests -iname "MongoSelectLowererTests.cs"`; add to it)

**Interfaces:**
- Consumes: `LookupExpression.PipelineKind` (Task 1).
- Produces: `AppendLookupStages` emits `MongoLookupStage` + `MongoUnwindStage(lookup,
  preserveNullAndEmptyArrays: true)` for any lookup with `PipelineKind == LookupPipelineKind.CorrelatedReducer`,
  and still throws for any other lookup that reaches the final `else` (unchanged).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void AppendLookupStages_emits_lookup_and_left_outer_unwind_for_CorrelatedReducer()
{
    var lookup = new LookupExpression(SampleNavigations.AnimalIdentificationMethods) // adjust to a real
                                                                                      // collection-nav fixture
    {
        PipelineKind = LookupPipelineKind.CorrelatedReducer
    };
    lookup.PipelineStages.Add(new BsonDocument("$limit", 1));

    var query = MongoSelectLowererTestHelpers.BuildQueryWithLookup(lookup); // match whatever the existing
                                                                             // tests in this file use to
                                                                             // construct a MongoQueryExpression
    var stages = MongoSelectLowerer.Lower(query); // or whatever the existing tests call — match convention

    Assert.Contains(stages, s => s is MongoLookupStage ls && ls.Lookup == lookup);
    Assert.Contains(stages, s => s is MongoUnwindStage { PreserveNullAndEmptyArrays: true } us && us.Lookup == lookup);
}
```

Match this test's exact construction calls to whatever `MongoSelectLowererTests.cs` already does to build a
`MongoQueryExpression`/`MongoSelectDefinition` for an existing lookup test (e.g. the reference-Include test
in that file) — don't invent new helper names; reuse the file's existing ones.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~AppendLookupStages_emits_lookup_and_left_outer_unwind_for_CorrelatedReducer"`
Expected: FAIL — throws `NativeTranslationNotSupportedException` (falls through to the current `else`).

- [ ] **Step 3: Implement**

In `MongoSelectLowerer.AppendLookupStages` (`MongoSelectLowerer.cs:342-404`), add a new `else if` branch
immediately before the final `else` (after the existing `ForceUnwind` branch, `:383-391`):

```csharp
else if (lookup.PipelineKind == LookupPipelineKind.CorrelatedReducer)
{
    // A reference-collection-nav First/FirstOrDefault projection leaf (EF-449). The $lookup's own
    // sub-pipeline has already narrowed to 0-or-1 matched documents (optional $match for a constant
    // predicate, optional $sort, then $limit:1) — $unwind here just flattens that 0-or-1-element array
    // to null-or-object. Always left-outer: the empty-vs-throw distinction between First and
    // FirstOrDefault is a READ-side concern (see MongoCorrelatedReducerLeaf), not a join-shape one.
    stages.Add(new MongoLookupStage(lookup));
    stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: true));
}
```

- [ ] **Step 4: Run test to verify it passes**

Same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Run full unit test project, then commit**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-449: lower CorrelatedReducer lookups to \$lookup + left-outer \$unwind"
```

---

### Task 4: Expose the FK-correlation-isolation helpers for reuse

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs`
- Test: none new (pure visibility change; covered by Task 1's and the existing suite's regression runs)

**Interfaces:**
- Produces: `ResolveCollectionNavigation(IEntityType outerEntityType, IEntityType targetEntityType,
  IReadOnlyList<ParameterExpression> predicateOwnParameters, Expression predicateBody)` — a reshaped,
  `internal static` overload callable without a `MethodCallExpression whereCall` (see below), and
  `internal static` visibility (from `private static`) on `CollectDependentPropertyNames`.

Investigation note carried from the design spec: `ResolveCollectionNavigation`
(`MongoProjectionBindingExpressionVisitor.Lookup.cs:332-359`) currently takes the whole `whereCall`
`MethodCallExpression` just to reach `whereCall.Arguments[1].UnwrapLambdaFromQuote()` for
`CollectDependentPropertyNames`. `NativeProjectionBinder`'s recognizer (Task 5) does not have a
`whereCall` — it has the nav-expanded predicate lambda's `Body`/`Parameters` directly (or, for the bare
`nav.FirstOrDefault().Member` shape with no user predicate, no predicate at all — see Task 5). Reshape the
signature so both call sites can use it:

- [ ] **Step 1: Reshape `ResolveCollectionNavigation` and widen `CollectDependentPropertyNames`'s visibility**

```csharp
// MongoProjectionBindingExpressionVisitor.Lookup.cs — replace the existing private static method
internal static INavigation ResolveCollectionNavigation(
    IEntityType outerEntityType,
    IEntityType targetEntityType,
    LambdaExpression correlationPredicate)
{
    var candidates = outerEntityType.GetNavigations()
        .Where(n => n.IsCollection && !n.IsEmbedded() && n.TargetEntityType == targetEntityType)
        .ToList();

    if (candidates.Count <= 1)
    {
        return candidates.Count == 1 ? candidates[0] : null;
    }

    var dependentKeyNames = CollectDependentPropertyNames(correlationPredicate);
    if (dependentKeyNames.Count == 0)
    {
        return null;
    }

    var byForeignKey = candidates
        .Where(n => n.ForeignKey.Properties.All(p => dependentKeyNames.Contains(p.Name)))
        .ToList();

    return byForeignKey.Count == 1 ? byForeignKey[0] : null;
}
```

Update this file's two existing call sites (`TryBindProjectedCollectionNavigation`,
`TryBindProjectedCollectionNavigationCount`) from
`ResolveCollectionNavigation(outerEntityType, targetEntityType, whereCall)` to
`ResolveCollectionNavigation(outerEntityType, targetEntityType, whereCall.Arguments[1].UnwrapLambdaFromQuote())`.

Change `private static HashSet<string> CollectDependentPropertyNames(LambdaExpression predicate)`
(`:367-372`) to `internal static`. No body change needed.

- [ ] **Step 2: Run full unit + relevant functional Include tests to confirm no regression**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Include"
```
Expected: all PASS, identical to before this task (pure refactor).

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs
git commit -m "EF-449: reshape ResolveCollectionNavigation for reuse outside the fallback visitor"
```

---

### Task 5: `NativeProjectionBinder.TryGetCorrelatedReducerLeaf` recognizer

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCorrelatedReducerLeaf.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.Lookup.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderTests.cs`
  (find via `find tests -iname "NativeProjectionBinderTests.cs"`; add to it, or create a sibling
  `NativeCorrelatedReducerLeafTests.cs` if the existing file is already very large — check its line count
  first)

**Interfaces:**
- Consumes: `MongoExpressionTranslator(IEntityType entityType)` (single-scope ctor,
  `MongoExpressionTranslator.cs:67`), `.TryTranslateField(Expression, out MongoFieldExpression)`,
  `.TryTranslate(Expression, out MongoExpression)`; `NativeCardinalityBinder.BuildEmptyBehavior(MongoReducerKind is not applicable here — see below, use the two-case form)`;
  `ResolveCollectionNavigation`/`CollectDependentPropertyNames` (Task 4);
  `MongoQueryExpression.AddLookup(LookupExpression)` (pre-existing, `MongoQueryExpression.Lookup.cs:~150`).
- Produces: `internal static bool TryGetCorrelatedReducerLeaf(MongoQueryExpression mongoQ,
  ParameterExpression outerParameter, Expression leafExpression, out MongoElementRefExpression? result)`
  on `NativeProjectionBinder`, called from `TryTranslateLeaf` (`NativeProjectionBinder.cs:531`); a new
  `internal sealed record MongoCorrelatedReducerLeaf(string Alias, LookupExpression Lookup, string Member,
  bool ThrowOnEmpty)`; `MongoQueryExpression.CorrelatedReducerLeaves` (a new `List<MongoCorrelatedReducerLeaf>`,
  consumed by Task 6).

**Step 1: Write the failing unit tests (accept/decline matrix)**

- [ ] **Step 1a: bare shape recognized**

```csharp
[Fact]
public void TryGetCorrelatedReducerLeaf_recognizes_bare_FirstOrDefault_member()
{
    // context.Set<Animal>().Select(a => a.IdentificationMethods.FirstOrDefault().Method)
    var (mongoQ, outerParam, leaf) = NativeProjectionBinderTestFixtures.BuildBareFirstOrDefaultMemberLeaf();
        // See fixture note below — mirror whatever this test file's existing helpers already do to build
        // a MongoQueryExpression + outer parameter + a LambdaExpression body for a given entity type.

    var accepted = NativeProjectionBinder.TryGetCorrelatedReducerLeaf(mongoQ, outerParam, leaf, out var result);

    Assert.True(accepted);
    Assert.NotNull(result);
    Assert.Single(mongoQ.CorrelatedReducerLeaves);
    Assert.False(mongoQ.CorrelatedReducerLeaves[0].ThrowOnEmpty);
}
```

Fixture note: build the `LambdaExpression` body directly via `Expression` APIs (not by compiling a real
LINQ query through the full pipeline) — mirror however `NativeSelectManyBinderTests` or
`NativeProjectionBinderTests` already construct a nav-expanded-shape tree by hand for their own recognizer
tests (grep `Expression.Call(.*FirstOrDefault\|OrderBy` in the existing
`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` tree for the closest precedent). Use
a real two-entity test model (a principal with a collection navigation to a dependent in a *separate*
collection, not owned) — check `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` for an existing shared
test model fixture with exactly this shape (a reference/FK-based one-to-many) before creating a new one.

- [ ] **Step 1b: predicate + order variants, and every decline case**

```csharp
[Fact]
public void TryGetCorrelatedReducerLeaf_recognizes_predicate_and_order_variant() { /* mirror 1a, add
    OrderBy + a CONSTANT-valued predicate to the built tree; assert accepted and that the built
    LookupExpression.PipelineStages contains three stages: $match(predicate), $sort, $limit:1, in that
    order */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_recognizes_First_as_ThrowOnEmpty() { /* mirror 1a using First
    instead of FirstOrDefault; assert mongoQ.CorrelatedReducerLeaves[0].ThrowOnEmpty is true */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_declines_parameterized_predicate() { /* build the predicate
    comparing to a captured variable/query parameter instead of a constant; assert TryGetCorrelatedReducerLeaf
    returns false and mongoQ.CorrelatedReducerLeaves is empty */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_declines_two_hop_navigation() { /* a.Nav1.Nav2.FirstOrDefault().Member;
    assert declines */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_declines_non_scalar_reduced_member() { /* nav.FirstOrDefault().SomeNestedEntity
    (not a scalar property); assert declines */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_declines_owned_collection_navigation() { /* an embedded/owned
    collection nav instead of a reference one; assert declines — this shape is handled by the pre-existing
    owned-collection machinery elsewhere in this file, not this recognizer */ }

[Fact]
public void TryGetCorrelatedReducerLeaf_declines_TPH_derived_target() { /* target entity type has a
    discriminator and is not its own root type; assert declines */ }
```

- [ ] **Step 2: Run tests to verify all fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryGetCorrelatedReducerLeaf"`
Expected: FAIL to compile (method doesn't exist yet).

- [ ] **Step 3: Create `MongoCorrelatedReducerLeaf`**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCorrelatedReducerLeaf.cs
namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Records the empty-input semantics for a projection leaf built by
/// <see cref="NativeTranslation.NativeProjectionBinder.TryGetCorrelatedReducerLeaf"/> (EF-449) — a
/// reference-collection-nav First/FirstOrDefault reduced to a scalar member inside a projection. The
/// $lookup's $unwind is always left-outer (see MongoSelectLowerer.AppendLookupStages's CorrelatedReducer
/// branch); ThrowOnEmpty distinguishes First (throws when the unwound field is absent) from FirstOrDefault
/// (a missing field already reads back as the member's own default — no extra work needed).
/// </summary>
internal sealed record MongoCorrelatedReducerLeaf(string Alias, LookupExpression Lookup, string Member, bool ThrowOnEmpty);
```

- [ ] **Step 4: Add the leaf list to `MongoQueryExpression`**

In `MongoQueryExpression.Lookup.cs`, alongside `_pendingLookups`:

```csharp
private readonly List<MongoCorrelatedReducerLeaf> _correlatedReducerLeaves = [];

/// <summary>See <see cref="MongoCorrelatedReducerLeaf"/>.</summary>
public IReadOnlyList<MongoCorrelatedReducerLeaf> CorrelatedReducerLeaves => _correlatedReducerLeaves;

internal void AddCorrelatedReducerLeaf(MongoCorrelatedReducerLeaf leaf) => _correlatedReducerLeaves.Add(leaf);
```

- [ ] **Step 5: Implement the recognizer**

Add to `NativeProjectionBinder.cs`, alongside `TryGetOwnedReferenceNavigationLeaf`:

```csharp
/// <summary>
/// Recognizes a reference-collection-nav reducer projected inline (EF-449) — e.g.
/// <c>animal.IdentificationMethods.FirstOrDefault().Method</c> — and, on a match, registers the
/// $lookup(+sub-pipeline)+$unwind this leaf needs and returns a <see cref="MongoElementRefExpression"/>
/// reading the reduced member off the unwound result.
/// </summary>
private static bool TryGetCorrelatedReducerLeaf(
    MongoQueryExpression mongoQ,
    ParameterExpression outerParameter,
    Expression leafExpression,
    [NotNullWhen(true)] out MongoElementRefExpression? result)
{
    result = null;

    // Peel the trailing scalar member: nav-chain-reduced.Member
    if (leafExpression is not MemberExpression { Expression: MethodCallExpression reducerCall } finalMember)
    {
        return false;
    }

    // reducerCall must be First/FirstOrDefault, optionally with a predicate, off Queryable/Enumerable.
    bool throwOnEmpty;
    Expression? predicateBody = null;
    ParameterExpression? predicateParam = null;
    switch (reducerCall.Method.Name)
    {
        case nameof(Queryable.First) or nameof(Enumerable.First):
            throwOnEmpty = true;
            break;
        case nameof(Queryable.FirstOrDefault) or nameof(Enumerable.FirstOrDefault):
            throwOnEmpty = false;
            break;
        default:
            return false;
    }

    Expression reducerSource = reducerCall.Arguments[0];
    if (reducerCall.Arguments.Count == 2
        && reducerCall.Arguments[1].UnwrapLambdaFromQuote() is LambdaExpression { Parameters: [var pp] } predLambda)
    {
        predicateParam = pp;
        predicateBody = predLambda.Body;
    }
    else if (reducerCall.Arguments.Count != 1)
    {
        return false;
    }

    // Optional single OrderBy/OrderByDescending immediately under the reducer.
    string? sortField = null;
    var sortAscending = true;
    if (reducerSource is MethodCallExpression
        {
            Method.Name: nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
        } orderCall)
    {
        sortAscending = orderCall.Method.Name == nameof(Queryable.OrderBy);
        reducerSource = orderCall.Arguments[0];
        // resolved against the element type below, once we know it.
        var sortKeyLambda = orderCall.Arguments[1].UnwrapLambdaFromQuote();
        if (sortKeyLambda is not LambdaExpression { Body: MemberExpression sortMember })
        {
            return false;
        }
        sortField = sortMember.Member.Name; // resolved to an IProperty below.
    }

    // reducerSource must now be the bare navigation access off the outer parameter.
    if (reducerSource is not MemberExpression { Expression: { } navReceiver } navMember
        || !IsSelectorParameter(navReceiver, outerParameter))
    {
        return false;
    }

    if (mongoQ.CollectionExpression.EntityType.FindNavigation(navMember.Member.Name) is not
        { IsCollection: true } navigation
        || navigation.IsEmbedded())
    {
        return false;
    }

    var targetEntityType = navigation.TargetEntityType;
    if (targetEntityType.FindDiscriminatorProperty() is not null
        && targetEntityType != targetEntityType.GetRootType())
    {
        return false; // TPH-derived target: out of scope (see design spec).
    }

    // The reduced element's own member must be a plain scalar property.
    if (targetEntityType.FindProperty(finalMember.Member.Name) is not { } memberProperty
        || memberProperty.IsPrimaryKey() && targetEntityType.FindPrimaryKey()!.Properties.Count > 1)
    {
        return false;
    }

    var lookup = new LookupExpression(navigation) { PipelineKind = LookupPipelineKind.CorrelatedReducer };

    if (predicateBody != null)
    {
        var elementTranslator = new MongoExpressionTranslator(targetEntityType);
        if (!elementTranslator.TryTranslate(predicateBody, out var predicateNode)
            || !IsConstantOnly(predicateNode))
        {
            return false; // parameterized predicate — PipelineStages has no placeholder substitution (see plan's Global Constraints).
        }

        var placeholders = new PlaceholderTable();
        var matchDoc = MongoQueryLanguageRenderer.Render(predicateNode, placeholders);
        if (placeholders.Count > 0)
        {
            return false; // defensive: TryTranslate should not have produced a parameter node here.
        }

        lookup.PipelineStages.Add(new BsonDocument("$match", matchDoc));
    }

    if (sortField != null)
    {
        if (targetEntityType.FindProperty(sortField) is not { } sortProperty)
        {
            return false;
        }

        lookup.PipelineStages.Add(new BsonDocument("$sort",
            new BsonDocument(sortProperty.GetElementName(), sortAscending ? 1 : -1)));
    }

    lookup.PipelineStages.Add(new BsonDocument("$limit", 1));

    mongoQ.AddLookup(lookup);
    mongoQ.AddCorrelatedReducerLeaf(new MongoCorrelatedReducerLeaf(
        Alias: finalMember.Member.Name, // overwritten by the caller with the real projection alias, see Step 6
        Lookup: lookup,
        Member: memberProperty.GetElementName(),
        ThrowOnEmpty: throwOnEmpty));

    result = new MongoElementRefExpression($"{lookup.As}.{memberProperty.GetElementName()}", finalMember.Type);
    return true;
}
```

Notes for the implementer:
- `IsConstantOnly(MongoExpression)` does not exist yet — write it as a small recursive walk over
  `MongoExpression` returning `false` the moment it encounters a `MongoParameterExpression`; check
  `MongoExpressionNegator`/`MongoAggregationExpressionRenderer` for the established pattern of walking this
  node hierarchy exhaustively over its concrete subtypes, and match that style (a `switch` expression over
  the sealed hierarchy, not a duck-typed reflection walk).
- `IsSelectorParameter` (`NativeProjectionBinder.cs`, used by `TryGetOwnedReferenceNavigationLeaf`) already
  exists — reuse it verbatim, do not reimplement.
- The `Alias` field placeholder above is provisional; Task 5 Step 6 below corrects it once wired into
  `TryTranslateLeaf`, which is the only call site that knows the real projection alias.

- [ ] **Step 6: Wire into `TryTranslateLeaf`**

In `NativeProjectionBinder.TryTranslateLeaf` (`NativeProjectionBinder.cs:531`), add a new arm — place it
after the plain-scalar-leaf arm (`:555-577`) and before the whole-root-entity leaf arm (`:610-617`), since
this leaf is a `MemberExpression` that the plain-scalar arm's `translator.TryTranslateField` will
correctly decline (no matching document field), so ordering relative to that arm doesn't matter, but it
must come before any arm that might otherwise mis-accept part of this shape:

```csharp
if (TryGetCorrelatedReducerLeaf(mongoQ, outerParameter, leafExpression, out var correlatedReducerLeaf))
{
    // Fix up the alias now that it's known — see Step 5's note.
    var lastLeaf = mongoQ.CorrelatedReducerLeaves[^1];
    mongoQ.ReplaceLastCorrelatedReducerLeafAlias(alias); // add this small helper to MongoQueryExpression.Lookup.cs:
                                                          // replaces _correlatedReducerLeaves[^1] with
                                                          // lastLeaf with { Alias = alias }
    result = correlatedReducerLeaf;
    return true;
}
```

Add the small helper referenced above to `MongoQueryExpression.Lookup.cs`:

```csharp
internal void ReplaceLastCorrelatedReducerLeafAlias(string alias)
    => _correlatedReducerLeaves[^1] = _correlatedReducerLeaves[^1] with { Alias = alias };
```

- [ ] **Step 7: Run tests to verify all pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryGetCorrelatedReducerLeaf"`
Expected: all PASS.

- [ ] **Step 8: Run full unit test project, then commit**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCorrelatedReducerLeaf.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.Lookup.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderTests.cs
git commit -m "EF-449: recognize reference-collection-nav First/FirstOrDefault projection leaf"
```

---

### Task 6: Read side — throw on empty for `First()`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
- Test: covered by Task 8's functional differential tests (empty-collection case with `First()`); add one
  focused unit test here if this visitor already has direct unit-test coverage elsewhere (check
  `find tests -iname "MongoProjectionBindingRemovingExpressionVisitorTests.cs"`) — otherwise Task 8's
  functional coverage is sufficient and this task's own "test" step is the functional one.

**Interfaces:**
- Consumes: `MongoQueryExpression.CorrelatedReducerLeaves` (Task 5).
- Produces: reading a projection alias that matches a `MongoCorrelatedReducerLeaf` with `ThrowOnEmpty ==
  true` throws `InvalidOperationException("Sequence contains no elements")` (matching
  `Enumerable.First`'s message) when the underlying `<lookup.As>` field is absent/null on the current
  document, instead of returning a CLR default.

**Investigation required before writing code (no shortcut available from prior research):** find the exact
method in `MongoProjectionBindingRemovingExpressionVisitor.cs` that reads a plain/computed field value by
alias for the DOM shaper (the generic per-projection-member BSON read, used by every other
scalar/computed leaf kind already, per Task 5's design rationale that no read-side change was expected for
the *ordinary* `FirstOrDefault` case). Locate it with:

```bash
grep -n "BsonDocument\[.*\]\|TryGetValue\|GetElement" src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs | head -30
```

- [ ] **Step 1: Write the failing functional test (skip ahead to Task 8's fixture; this task's own commit
  can be validated by that test once both are written — do not block Task 6's commit on Task 8 existing;
  instead write a minimal standalone reproduction here)**

```csharp
// Add to whatever functional test class Task 8 creates (NativeCorrelatedReducerProjectionTests) —
// if Task 8 hasn't been done yet, create the file now with just this one test and expand it in Task 8.
[Fact]
public async Task First_over_empty_reference_collection_throws()
{
    // Arrange: a principal with zero related documents in the target collection.
    // (Use this project's usual TemporaryDatabaseFixture pattern — see any existing
    // NativeSelectManyTests test for the exact setup shape to copy.)

    var context = /* ... */;
    var query = context.Set<Animal>()
        .Where(a => a.Id == animalWithNoIdentificationMethods.Id)
        .Select(a => new { a.Id, a.IdentificationMethods.First().Method });

    await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run with the appropriate `--filter` for this new test. Expected: FAIL — either returns a default value
silently, or throws a different exception (e.g. a BSON "element not found" error) instead of
`InvalidOperationException`.

- [ ] **Step 3: Implement the throw**

At the read call site found by the grep above, add a check: before returning the read value for a
projection alias, look it up in `mongoQ.CorrelatedReducerLeaves` (via the alias); if found with
`ThrowOnEmpty == true` and the underlying lookup field (`leaf.Lookup.As`) is absent or BSON-null on the
current document, throw:

```csharp
throw new InvalidOperationException("Sequence contains no elements");
```

Exact insertion point depends on what Step 3's investigation finds — this must be a per-value check
keyed by alias, not a blanket change to the read path (every other leaf kind must be completely
unaffected).

- [ ] **Step 4: Run test to verify it passes**

Same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Run full unit + functional Query suites, then commit**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCorrelatedReducerProjectionTests.cs
git commit -m "EF-449: throw on empty for First() over a reference-collection nav projection leaf"
```

---

### Task 7: Functional differential-correctness test suite

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCorrelatedReducerProjectionTests.cs`
  (created in Task 6 with one test; expand here)

**Interfaces:**
- Consumes: everything from Tasks 1-6.

- [ ] **Step 1: Write the full differential-correctness `[Theory]` matrix**

Follow the established pattern for this file family (`NativeOwnedCollectionAllTests`/
`NativeOwnedCollectionCountTests` per Query `AGENTS.md`'s "How to test" section) — assert the native
result equals an in-memory LINQ oracle evaluated over the *same* `Expression` object. Cases to cover, each
against a fixture with: multiple candidate related rows (to prove ordering/first-pick matters), zero
related rows, a predicate matching zero/one/many rows, and **two** outer documents (to prove per-document
correlation, not a global first):

```csharp
[Theory]
[MemberData(nameof(CorrelatedReducerShapes))]
public async Task Native_result_matches_LINQ_oracle(Expression<Func<IQueryable<Animal>, IQueryable<object>>> queryShape)
{
    using var nativeContext = CreateContext(MongoQueryMode.NativeOnly);
    using var oracleContext = CreateContext(MongoQueryMode.DriverLinq); // or an in-memory provider if this
                                                                         // repo's convention for these
                                                                         // oracle tests uses one — check
                                                                         // NativeOwnedCollectionAllTests
                                                                         // for the exact oracle mechanism
                                                                         // used elsewhere in this file family

    var compiledShape = queryShape.Compile();
    var nativeResult = await compiledShape(nativeContext.Set<Animal>()).ToListAsync();
    var oracleResult = compiledShape(oracleContext.Set<Animal>().AsEnumerable().AsQueryable()).ToList();

    nativeResult.Should().BeEquivalentTo(oracleResult); // or Assert.Equal — match this file family's
                                                         // existing assertion convention
}

public static IEnumerable<object[]> CorrelatedReducerShapes()
{
    yield return [(Expression<Func<IQueryable<Animal>, IQueryable<object>>>)(q =>
        q.Select(a => new { a.Id, a.IdentificationMethods.FirstOrDefault().Method }))];
    yield return [(Expression<Func<IQueryable<Animal>, IQueryable<object>>>)(q =>
        q.Select(a => new { a.Id, a.IdentificationMethods.FirstOrDefault(m => m.Method == IdentificationMethod.EarTag).Method }))];
    yield return [(Expression<Func<IQueryable<Animal>, IQueryable<object>>>)(q =>
        q.Select(a => new { a.Id, a.IdentificationMethods.OrderByDescending(m => m.Id).FirstOrDefault().Method }))];
}
```

Note: since this feature has **no driver-LINQ oracle** (confirmed — the shape hard-fails on
`MongoQueryMode.DriverLinq` too), the "oracle" comparison above must NOT use `MongoQueryMode.DriverLinq`
against the real provider (it will throw). Use whichever in-memory comparison mechanism
`NativeOwnedCollectionAllTests`/`NativeOwnedCollectionCountTests` actually use for their own oracle
comparisons (check those files directly — per the design spec's own citation of this pattern, they likely
already solve the "no driver-LINQ baseline" oracle problem some other way, e.g. comparing against
plain-C#-collection LINQ over pre-seeded in-memory data rather than a second DB round-trip). Copy
whatever mechanism they use rather than inventing a new one.

- [ ] **Step 2: Run tests to verify they fail (pre-implementation baseline check)**

This step should already pass given Tasks 1-6 are done — if instead these theory cases fail here, that
means Tasks 1-6 have a gap; do not proceed to Task 8 until they pass.

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeCorrelatedReducerProjectionTests"`
Expected: PASS (this task only adds coverage; Tasks 1-6 already made the underlying feature work).

- [ ] **Step 3: Add `NativeOnly`-mode proof and decline-matrix assertions**

```csharp
[Fact]
public async Task Correlated_reducer_projection_succeeds_under_NativeOnly()
{
    using var context = CreateContext(MongoQueryMode.NativeOnly);
    var result = await context.Set<Animal>()
        .Select(a => new { a.Id, a.IdentificationMethods.FirstOrDefault().Method })
        .ToListAsync();

    result.Should().NotBeEmpty();
}

[Fact]
public async Task Two_hop_navigation_still_declines_with_original_exception()
{
    using var context = CreateContext(MongoQueryMode.Native); // default mode — no working fallback exists
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        context.Set<SomeType>()
            .Select(x => new { x.NavA.NavB.FirstOrDefault().SomeMember })
            .ToListAsync());
}
```

- [ ] **Step 4: Run full functional Query suite, then commit**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCorrelatedReducerProjectionTests.cs
git commit -m "EF-449: differential-correctness and NativeOnly coverage for correlated reducer projection"
```

---

### Task 8: Flip the EF-216-tagged spec test

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Mapping/BuiltInDataTypesMongoTest.cs:44-46,65-67`

**Interfaces:**
- Consumes: everything from Tasks 1-7.

- [ ] **Step 1: Flip the EF9/EF10 override to a real pass**

Change (both the `!EF8` async override at `:44-46` and check whether it needs its own `#if EF9`/`#else`
split the way `Can_read_back_bool_mapped_as_int_through_navigation` does just above it, per the design
spec's testing note — **do not assume symmetry, verify empirically**):

```csharp
// Fails: Cross-document navigation access issue EF-216
public override Task Can_read_back_mapped_enum_from_collection_first_or_default()
    => AssertTranslationFailed(() => base.Can_read_back_mapped_enum_from_collection_first_or_default());
```

to:

```csharp
public override Task Can_read_back_mapped_enum_from_collection_first_or_default()
    => base.Can_read_back_mapped_enum_from_collection_first_or_default();
```

- [ ] **Step 2: Run the test on EF10**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Can_read_back_mapped_enum_from_collection_first_or_default"`
Expected: PASS. If it fails, **stop and diagnose** — do not adjust the recognizer's scope to force a pass
without understanding why (check whether `IdentificationMethods`/`IdentificationMethod.Method` in the
upstream `BuiltInDataTypesTestBase` fixture matches every assumption Task 5's recognizer makes: not
embedded, not TPH-derived, `Method` a plain scalar enum property).

- [ ] **Step 3: Repeat for EF9**

Run the same filter with `-c "Debug EF9"`. If it passes, remove the `#if EF9` / `#else` split entirely (use
one unconditional override, matching the EF10 one) unless `Can_read_back_bool_mapped_as_int_through_navigation`
right above it still needs the split for its own (unrelated, join-based) reasons — leave that one
untouched either way, this task only touches its own test.

- [ ] **Step 4: Try EF8**

Run the same filter with `-c "Debug EF8"`. If it passes, apply the same override to the `#else` (EF8) half
of the class (`:65-67`) too. If it fails, leave the EF8 half as `AssertTranslationFailed` (matching this
class's existing convention for EF8-only gaps) and add a one-line comment noting which EF-version-specific
mechanism (LINQ shape, nav-expansion difference) is the reason — investigate before writing that comment.

- [ ] **Step 5: Update `docs/failing-spec-tests.md` if this test is tracked there**

```bash
grep -n "Can_read_back_mapped_enum_from_collection_first_or_default" docs/failing-spec-tests.md
```

If present, remove that line (per this repo's "no skip, baseline via `// Fails` + doc entry" convention —
a test that now genuinely passes has nothing left to document there).

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Mapping/BuiltInDataTypesMongoTest.cs \
        docs/failing-spec-tests.md
git commit -m "EF-449: Can_read_back_mapped_enum_from_collection_first_or_default now translates natively"
```

---

### Task 9: Full multi-EF regression sweep, squash, and land on `NativeQueryOngoing`

**Files:** none (verification + git history operation only)

- [ ] **Step 1: Full three-EF-version build + test sweep**

Invoke the `/test-all` skill (`.claude/skills/test-all/`), or run manually:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

Expected: **zero** `Passed→Failed` regressions versus the branch tip before this plan started. In
particular, per the design spec's stated boundary, confirm byte-for-byte that:
- TPH-narrowed collection `Include` tests are unaffected (still fallback, identical MQL).
- Nested/`ThenInclude` lookup tests (`Ef37*` families referenced in Query `AGENTS.md`) are unaffected.

- [ ] **Step 2: Also run with the native-only coverage instrument**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
```

Confirm the pass count increased by exactly the number of newly-native spec-test cases this feature
touches (at minimum, the one flipped in Task 8), with zero new failures elsewhere.

- [ ] **Step 3: Squash onto `NativeQueryOngoing`**

Per this repo's stacked-PR convention: confirm the current branch and the target rolling branch.

```bash
git branch --show-current
git log --oneline main..HEAD   # review exactly what this plan's commits are, on top of what was already there
```

Squash this plan's commits (Tasks 1-8) into **one** commit, then fast-forward `NativeQueryOngoing` onto it
— follow the exact mechanics recorded in this repo's own stacked-PR workflow memory/convention (squash the
slice, then `ff-only` onto the rolling branch, then push), keeping a `-presquash` backup branch before
squashing, matching this repo's established safety habit for this operation. Do not invent a different
git sequence — ask if the exact commands aren't already clear from repo convention/CLAUDE.md at execution
time.

- [ ] **Step 4: Update the EF-449 Jira ticket and EF-322 status doc if one is tracked**

```bash
grep -n "EF-449\|EF-216" docs/native-query-status-EF-322.md
```

If `docs/native-query-status-EF-322.md` exists and tracks per-ticket status (per project memory, this repo
maintains a consolidated native-query status doc), add a line for EF-449's completion. Transition EF-449 in
Jira to whatever status this repo's convention uses for a shipped-but-unreleased slice (check a recently
closed sibling ticket like EF-421 or EF-441 for the exact transition name before applying it).
