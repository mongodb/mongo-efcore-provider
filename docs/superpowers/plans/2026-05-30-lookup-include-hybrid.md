# Hybrid `$lookup` Include Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make server-side MongoDB `$lookup`/`$unwind` the default execution strategy for cross-collection `Include`, while keeping the existing client-side fan-out as an automatic fallback for shapes `$lookup` cannot express (collection→collection `ThenInclude`, many-to-many), and make filtered `Include` work on **both** paths.

**Architecture:** Port the `$lookup` execution model from the `damieng/poc-include` branch ("B") into the current `EF-117a` branch ("A"). A central classifier decides per-`IncludeExpression` whether to emit a server-side `$lookup` (registered on `MongoQueryExpression` and appended as an aggregation stage by `MongoEFToLinqTranslatingExpressionVisitor`, mirroring the existing VectorSearch `AppendStage` pattern) or to fall back to A's `MongoIncludeCompiler.LoadCollection`/`LoadReference` fan-out. The shaper reads `$lookup` array/`$unwind` object results instead of issuing sub-queries when the server-side path is chosen.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core 8/9/10 (multi-config via `EF8`/`EF9`/`EF10` define constants), MongoDB C# Driver LINQ v3, xUnit + FluentAssertions. MQL assertions via `AssertMql`.

---

## Why this matters (rationale)

The hybrid is not only a performance change — server-side `$lookup` delivers two
improvements over the shipping fan-out, both documented as known issues in
`docs/EF-117-include-overview.md`:

1. **Eliminates the N+1 round-trip cost** — the headline perf risk. Fan-out issues
   one sub-query per principal/dependent (`Orders.Include(o => o.Customer)` =
   830 round-trips on Northwind). `$lookup` collapses this to a single query.
2. **Fixes the `AsNoTrackingWithIdentityResolution` correctness gap.** Fan-out
   materializes each sub-query in its own scope, so identity resolution does not
   span the outer query and the sub-queries (two dependents pointing at the same
   principal resolve to two instances). `$lookup` is a *single* materialization,
   so identity resolution works for free on that path. This is a **functional
   correctness** win, not just performance.

**Honest design caveat (the cost of `$lookup`):** a collection `$lookup` produces a
nested array per principal, materialized server-side into one response. On
high-cardinality data (e.g. 100 customers × ~100 orders ≈ 10k embedded docs, or
deep chained lookups) this can approach MongoDB's **16 MB BSON document limit** and
shifts cost from client round-trips to cluster memory/CPU. The fan-out fallback is
the natural escape hatch for these cases; this caveat is why a future explicit
`AsSplitQuery()` opt-out (Stage 9, out of scope here) is worth keeping on the
roadmap. Record this caveat in `docs/EF-117-include-overview.md` in Stage 8.

---

## Critical context — read before starting

1. **This is a port, not a `git merge`.** A (`EF-117a`) is based on current `main` (`ed0fd2c`); B (`damieng/poc-include`) is based on an older `main` (`d4aab4d`) and lives in a different fork. The two branches modify **different files** for Include (A added `MongoIncludeCompiler` + an `IncludeJoinUnwrapper` in the preprocessor and left `MongoEFToLinqTranslatingExpressionVisitor` untouched; B heavily rewrote `MongoEFToLinqTranslatingExpressionVisitor`, `MongoProjectionBindingExpressionVisitor`, the `Expressions/*`, and changed a convention). A literal merge will conflict destructively. **Port file-by-file**, reading B's source as the reference implementation.

2. **B's remote is already fetched** into this repo as `damieng/poc-include`. Read any B file with:
   `git show damieng/poc-include:<path>`
   Read B's diff for a file with:
   `git diff d4aab4d..damieng/poc-include -- <path>`

3. **The work happens on a new branch `EF-117c`, branched off the current `EF-117b` head.** `EF-117b` already exists and is the current branch — it is `EF-117a` plus the Stage-5-final commit (`4ce46d0`, "zero spec-test failures across EF8/9/10") and two review-iteration commits (`8aaf8e5`, `ade279b`). Branching `EF-117c` off `EF-117b` inherits those review fixes. Do not touch `EF-117b` directly until the end.

4. **Respect the prior EF-117 review resolutions** baked into `8aaf8e5` / `ade279b`. When porting B's code, do **not** re-introduce patterns those reviews removed — specifically, use canonical `QueryableMethods.Select` / `QueryableMethods.Join` reference-equality and **structural** `TransparentIdentifier` checks, never B's `Method.Name == nameof(...)` string-name or type-name-string patterns. (Porting-convention rule, applies to every `$lookup`/join port below.)

5. **A's existing fan-out must keep passing throughout.** Every stage is gated so that until the `$lookup` path is proven for a shape, that shape still uses fan-out. The **full spec project on all three EF targets** is the regression oracle (current floor: 0 failures — see Stage 0), not just `NorthwindInclude*`.

6. **MongoDB is required** for functional/spec tests **and for every MQL-asserting test** (`AssertMql` captures MQL by *running* the query via `TestMqlLoggerFactory`). If `MONGODB_URI`/`ATLAS_URI` are unset, a Docker container is auto-started (see `tests/.../FunctionalTests/Utilities/TestServer.cs`). **Confirm Docker is running before any stage that adds MQL assertions** (Stages 2–8).

7. **Pipeline-form `$lookup`** (Stage 6, `let` + `$expr` + appended stages) requires MongoDB 3.6+. The driver's minimum supported server is well above this, so it is safe — confirm explicitly in Stage 6 against the driver compatibility matrix.

### Routing decision table (the contract this plan implements)

| Include shape | Execution path | Stage |
|---|---|---|
| Collection, principal→dependents (`Customer.Orders`) | **`$lookup`** (array field `_lookup_<Nav>`) | 2 |
| Reference, dependent→principal (`Order.Customer`) | **`$lookup` + `$unwind`** (`preserveNullAndEmptyArrays`) | 3 |
| Multi-level reference chains / reference+collection on same root | **chained `$lookup`(+`$unwind`)** with `_outer`/`_inner` prefixing | 4 |
| Composite-key FK target (`OrderDetails`) | `$lookup` on `_id.<field>` path | 5 |
| Filtered Include (`Where`/`OrderBy`/`Skip`/`Take` in nav) | **pipeline-form `$lookup`** (server) AND filtered fan-out (client) | 6 |
| **Collection→collection `ThenInclude`** (`Orders.ThenInclude(o => o.Items)`) | **fan-out fallback** (A's `LoadCollection`) | 7 |
| **Many-to-many / skip navigations** | **fan-out fallback or clean rejection** (A's current behavior) | 7 |
| Shadow-key / shadow-FK | clean rejection (A's current behavior, unchanged) | — |

---

## File structure

**New files (this plan):**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs` — models a pending `$lookup` (ported from B, adapted).
- `src/MongoDB.EntityFrameworkCore/Query/IncludeStrategy.cs` — enum + classifier helper (`ServerLookup` vs `ClientFanOut`).
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/LookupExpressionTests.cs` — unit tests for `$lookup` field derivation. (Per review R1, MQL-content assertions live in the existing spec suites where `AssertMql` is wired; materialization tests extend the existing `IncludeTests.cs` — no duplicate test model.)

**Modified files:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.cs` — add `PendingLookups` / `AddLookup` / `UsesDriverJoinFields`.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` — add `AppendLookupStages` (mirror of `ProcessVectorSearch`'s `AppendStage` use).
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoIncludeCompiler.cs` — add `ShouldUseLookup(...)` classifier; keep `LoadCollection`/`LoadReference` for fallback; extend `ExtractIncludeChainPath` to capture filter/order/paging (Stage 6).
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` — in the cross-collection Include branch, route: register `LookupExpression` + rewrite shaper for the `$lookup` path, or preserve the `IncludeExpression` for fan-out.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` — shaper reads of `$lookup` array / `$unwind` object (port B's reads) for the server path; unchanged loader-call emission for the fan-out path.
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/EntityProjectionExpression.cs`, `ObjectAccessExpression.cs`, `ObjectArrayProjectionExpression.cs` — port B's `_lookup_<Nav>` field-binding additions.
- `src/MongoDB.EntityFrameworkCore/Metadata/Conventions/MongoRelationshipDiscoveryConvention.cs` — convention reconciliation (Stage 8, gated decision).
- The 4 spec suites `NorthwindInclude*QueryMongoTest.cs` + `failing-spec-tests.md` — updated in Stage 8.

---

## Conventions for every task

- **Branch:** all work on `EF-117c`, branched off `EF-117b` (created in Stage 0).
- **Build one EF version while iterating:** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`.
- **Functional smoke:** `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~IncludeTests"`.
- **Spec regression oracle:** `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindInclude"`.
- **Multi-EF at stage boundaries:** invoke the `/test-all` skill.
- **Commit after every green step.** First commit message + PR title start with `EF-117:`.

---

## Stage 0 — Setup, routing seam, characterization tests

**Goal:** create the branch, add the strategy enum and a single routing decision point, and lock in current behavior with characterization tests so no regression slips through. No behavior change yet (classifier returns `ClientFanOut` for everything).

> **Stage 0 floor (measured 2026-05-30 on `EF-117c`, tree identical to `ade279b`):**
> | Config | Failed | Passed | Skipped |
> |---|---|---|---|
> | Debug EF10 (full spec project) | 0 | 4418 | 14 | — re-run in this environment (44 s) |
> | Debug EF8 | 0 | 4714 | 11 | — from `ade279b` (identical tree; full `/test-all` at Stage 8) |
> | Debug EF9 | 0 | 4858 | 11 | — from `ade279b` (identical tree; full `/test-all` at Stage 8) |
> Plus `IncludeTests` 10/10, `OwnedEntityTests` 70/70, `UnitTests` 260/260. **0 failures is the floor at every later stage boundary.**

### Task 0.1: Create the working branch

**Files:** none (git only).

- [ ] **Step 1: Branch EF-117c off the current EF-117b head**

```bash
cd ~/code/mongo-efcore-provider
git checkout EF-117b           # already current; ensures we inherit 8aaf8e5/ade279b
git checkout -b EF-117c
git fetch damieng poc-include  # ensure B is available as a read reference
```

- [ ] **Step 2: Confirm the FULL baseline is green on all three EF targets**

The floor is the entire spec project plus functional/unit suites, on EF8/EF9/EF10 — **not** just `NorthwindInclude` on EF10. Many non-Include suites (`NorthwindQueryFilters*`, `NorthwindWhere*`, `NorthwindSelect*`, `Mapping/BuiltInDataTypesMongoTest`) had EF-117 overrides updated in the staged work and could regress from this port.

Run on EF10: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
`dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build`
Then invoke the **`/test-all` skill** to confirm EF8 + EF9.
Expected (current EF-117b floor, from commit `ade279b` — record exact counts):

| Config | Expected |
|---|---|
| Debug EF8  | 0 failed / ~4714 passed, 11 skipped |
| Debug EF9  | 0 failed / ~4858 passed, 11 skipped |
| Debug EF10 | 0 failed / ~4418 passed, 14 skipped |

Plus `IncludeTests` (10/10 EF10), `OwnedEntityTests` (70/70), `UnitTests` (260/260). **Zero failures on all three is the non-negotiable floor at every later stage boundary.**

- [ ] **Step 3: Record the floor**

Record the per-config counts in `docs/superpowers/plans/2026-05-30-lookup-include-hybrid.md` (a "Stage 0 floor (measured)" note) so reviewers can compare.

### Task 0.1b: Pre-existing BREAKING-CHANGES debt (surfaced by review R6)

**Files:** Modify `BREAKING-CHANGES.md`

This is **pre-existing debt from the shipping EF-117 work**, not introduced by the hybrid — the api-stability-reviewer's iter-2 finding flagged that the exception-type changes already in the code (M2M skip-nav → `InvalidOperationException`; shadow/composite key → `NotSupportedException`) are missing from `BREAKING-CHANGES.md`. Clean it up before the hybrid adds more.

- [ ] **Step 1:** Add `BREAKING-CHANGES.md` entries for the M2M and shadow/composite-key exception-type changes already present as of `ade279b`.
- [ ] **Step 2: Commit**

```bash
git commit -am "EF-117: Document pre-existing Include exception-type breaks in BREAKING-CHANGES"
```

### Task 0.2: Add the `IncludeStrategy` enum and routing predicate

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/IncludeStrategy.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoIncludeCompiler.cs` (add `ShouldUseLookup`)

- [ ] **Step 1: Write the failing test** (add to a new unit test class)

Per review R2, do **not** introduce a misleading `IncludeStrategyDefaults.Default`
constant — `ChooseStrategy` makes its own per-include decision and never consults a
"default" constant. Test the *actual* contract: a representative include classifies
to fan-out at Stage 0. Create
`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/IncludeStrategyTests.cs`:

```csharp
public class IncludeStrategyTests
{
    [Fact]
    public void Stage0_classifier_routes_cross_collection_include_to_fan_out()
    {
        // Build a model with a cross-collection collection navigation, get its
        // IncludeExpression + INavigation, and assert the Stage-0 classifier
        // returns ClientFanOut (preserves current behavior until later stages).
        var (includeExpression, navigation) = BuildCollectionIncludeFixture();
        Assert.Equal(IncludeStrategy.ClientFanOut,
            MongoIncludeCompiler.ChooseStrategy(includeExpression, navigation));
    }
}
```

(For the fixture, reuse the standalone `MongoQueryExpression` construction pattern
from `tests/.../UnitTests/Query/Expressions/MongoQueryExpressionTests.cs` — a
`DbContext` with `UseMongoDB` builds the model without connecting.)

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~IncludeStrategyTests"`
Expected: FAIL — `IncludeStrategy` / `ChooseStrategy` do not exist (build error).

- [ ] **Step 3: Create the enum** (no `Default` constant — R2)

`src/MongoDB.EntityFrameworkCore/Query/IncludeStrategy.cs`:

```csharp
namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Selects how a cross-collection <c>Include</c> is executed.
/// </summary>
internal enum IncludeStrategy
{
    /// <summary>Server-side MongoDB <c>$lookup</c> (single query).</summary>
    ServerLookup,

    /// <summary>Client-side fan-out: one sub-query per principal (EF-117 Stage 1–4).</summary>
    ClientFanOut
}
```

- [ ] **Step 4: Add the routing predicate to `MongoIncludeCompiler`**

Add to `MongoIncludeCompiler` (next to `IsCrossCollection`):

```csharp
/// <summary>
/// Decides whether a cross-collection include should use a server-side
/// <c>$lookup</c> or fall back to the client-side fan-out loader.
/// Stage 0: always fan-out. Later stages enable shapes incrementally.
/// </summary>
public static IncludeStrategy ChooseStrategy(IncludeExpression includeExpression, INavigation navigation)
{
    // Stage 0 placeholder — every shape still fans out. Do not add real
    // routing here until the matching stage's $lookup path is implemented.
    return IncludeStrategy.ClientFanOut;
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~IncludeStrategyTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/IncludeStrategy.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoIncludeCompiler.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/IncludeStrategyTests.cs
git commit -m "EF-117: Add IncludeStrategy enum and routing seam (default fan-out)"
```

### Task 0.3: Decide test placement (avoid model duplication — R1)

Per review R1, do **not** create a parallel `LookupIncludeTests.cs` that duplicates
`IncludeTests.cs`'s Customer/Order model. Two homes for the new tests:

- **Entity-materialization tests** (does the navigation populate correctly?) →
  **extend the existing `tests/.../FunctionalTests/Query/IncludeTests.cs`** with new
  `[Fact]`s reusing its model + `IgnoreCacheKeyFactory` plumbing.
- **MQL-content assertions** (is a `$lookup` emitted with the right shape?) →
  **the spec suites** (`NorthwindInclude*QueryMongoTest.cs`), where `AssertMql` and
  `TestMqlLoggerFactory` are already wired. The functional project does not currently
  have the MQL-capture logger hook; do not rebuild it there.

- [ ] **Step 1: Add a characterization `[Fact]` to `IncludeTests.cs`** pinning current fan-out materialization (guards against regression as routing flips):

```csharp
[Fact]
public async Task Collection_include_materializes_regardless_of_strategy()
{
    using var db = CreateContext();
    var customers = await db.Customers.Include(c => c.Orders).ToListAsync();
    customers.Should().NotBeEmpty();
    customers.Should().OnlyContain(c => c.Orders != null);
}
```

- [ ] **Step 2: Run to verify it passes on the current fan-out implementation**

Run: `dotnet test ... --filter "FullyQualifiedName~IncludeTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git commit -am "EF-117: Pin current Include materialization behavior (characterization)"
```

---

## Stage 1 — Port the `$lookup` plumbing (emission only, default OFF)

**Goal:** bring `LookupExpression`, `MongoQueryExpression.PendingLookups`, and `MongoEFToLinqTranslatingExpressionVisitor.AppendLookupStages` into A. Nothing routes to it yet — verify in isolation that a manually-registered lookup produces the correct `$lookup` BSON in the MQL.

### Task 1.1: Port `LookupExpression`

**Files:** Create `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs`

- [ ] **Step 1: Write a failing unit test for `LookupExpression` field derivation**

This is genuinely unit-testable (it only needs a model, no translator/live Mongo) and
is the unit-level coverage that replaces the impractical translator-MQL test (B3). Add
to `tests/.../UnitTests/Query/Expressions/LookupExpressionTests.cs`, using the
standalone model-build pattern from `MongoQueryExpressionTests.cs`:

```csharp
[Fact]
public void Reference_navigation_derives_local_and_foreign_fields()
{
    var nav = GetNavigation<Order>(nameof(Order.Customer));   // dependent-side reference
    var lookup = new LookupExpression(nav);
    lookup.From.Should().Be("Customers");
    lookup.As.Should().Be("_lookup_Customer");
    lookup.ShouldUnwind.Should().BeTrue();         // IsReference => unwind
    lookup.LocalField.Should().Be("CustomerId");   // FK on dependent
}

[Fact]
public void Composite_key_target_paths_into_id_subdocument()
{
    var nav = GetNavigation<Order>(nameof(Order.OrderDetails)); // composite-key dependent
    var lookup = new LookupExpression(nav);
    lookup.ForeignField.Should().Be("_id.OrderID");
}
```

Run: `dotnet test ...UnitTests... --filter "FullyQualifiedName~LookupExpressionTests"` → FAIL (type missing).

- [ ] **Step 2: Copy B's `LookupExpression` verbatim, then audit field-path helpers**

Source: `git show damieng/poc-include:src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs`. Copy it as-is (it is self-contained: constructor derives `From`/`LocalField`/`ForeignField`/`As`, with `GetFieldPath` handling composite-key `_id.<field>` paths, plus `PipelineStages`/`HasPipeline`/`IsReference`/`ShouldUnwind`/`ForceUnwind`). If `GetElementName`/`GetCollectionName` differ on A's base, adapt the calls.

- [ ] **Step 3: Run unit test + build**

Run the test → PASS. `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → compiles.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/LookupExpressionTests.cs
git commit -m "EF-117: Port LookupExpression from poc-include with field-derivation unit tests"
```

### Task 1.2: Add `PendingLookups` to `MongoQueryExpression`

**Files:** Modify `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.cs`

- [ ] **Step 1: Port B's additions**

From `git diff d4aab4d..damieng/poc-include -- src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.cs`, add:

```csharp
private readonly List<LookupExpression> _pendingLookups = [];

/// <summary>Pending $lookup stages for cross-collection Include operations.</summary>
public IReadOnlyList<LookupExpression> PendingLookups => _pendingLookups;

/// <summary>Register a $lookup stage (de-duplicated by output alias).</summary>
public void AddLookup(LookupExpression lookup)
{
    if (_pendingLookups.All(l => l.As != lookup.As))
        _pendingLookups.Add(lookup);
}

/// <summary>True for Include-generated LeftJoins (driver _outer/_inner fields present).</summary>
public bool UsesDriverJoinFields { get; set; }
```

(Skip B's `AddInnerCollection`/`CapturedExpression` for now — those belong to B's explicit-Join path, not needed for the Include-only port. Revisit in Stage 4 if multi-level needs them.)

- [ ] **Step 2: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: compiles.

- [ ] **Step 3: Commit**

```bash
git commit -am "EF-117: Add PendingLookups registry to MongoQueryExpression"
```

### Task 1.3: Port `AppendLookupStages` into the EF→LINQ translator

**Files:** Modify `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs`

- [ ] **Step 1: Locate the VectorSearch `AppendStage` pattern to mirror**

The translator already builds `MongoQueryable.AppendStage` calls for VectorSearch around line 383 (`ProcessVectorSearch`). The `$lookup`/`$unwind` emission uses the identical mechanism. Read B's `AppendLookupStages` (lines 736–810 of `git show damieng/poc-include:src/.../MongoEFToLinqTranslatingExpressionVisitor.cs`) — it builds `$lookup` `BsonDocument`s (localField/foreignField form, and pipeline form when `HasPipeline`) plus a `$unwind` (`preserveNullAndEmptyArrays: true`) when `ShouldUnwind`.

> **No test at this step (B3).** There is no harness to run
> `MongoEFToLinqTranslatingExpressionVisitor` to an MQL string standalone — the
> translator needs a live `QueryContext` (MongoClient), `BsonSerializerFactory`, and
> the full `MongoQueryCompilationContext`, and every existing query unit test goes
> end-to-end through `DbContext`. Constructing the translator in isolation is not
> practical. So Stage 1.3 only confirms the code **compiles and is wired into the
> call chain**; first real `$lookup`-emission verification is Stage 2's MQL test,
> which lands within one task. (Trade-off accepted: a regression in
> `AppendLookupStages` with no routing change is not caught by Stage 1 alone.)

- [ ] **Step 2: Port `AppendLookupStages` and call it from `Translate`/`TranslateProjected`**

Add the `_pendingLookups` field to the translator (sourced from the `MongoQueryExpression`), and the `AppendLookupStages(Expression query)` method (port from B, lines 739–810 — but per porting-convention §4, use canonical `QueryableMethods`/structural checks, not B's string-name patterns). Call it at the end of the translator's terminal-expression assembly, exactly where B calls it (after building the base query, before `ApplyAsSerializer`/`As`). The translator already has the `appendStageMethod`/`stageConstructor`/`serializerType` reflection from `ProcessVectorSearch` — reuse those helpers rather than duplicating.

- [ ] **Step 3: Build to verify it compiles and is wired**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: compiles; `AppendLookupStages` is reachable from the translate path (verify by reading the call site, since `_pendingLookups` is empty until Stage 2 routes anything).

- [ ] **Step 4: Run full spec regression** (must still be at the Stage-0 floor — nothing routes to lookup yet)

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build`
Expected: 0 failures (full floor maintained).

- [ ] **Step 5: Commit**

```bash
git commit -am "EF-117: Emit $lookup/$unwind aggregation stages (AppendLookupStages), unused by routing"
```

---

## Stage 2 — Server-side collection include (`Customer.Orders`)

**Goal:** route principal→dependents collection includes to `$lookup` and read the `_lookup_<Nav>` array in the shaper. Fan-out remains the fallback for everything else.

### Task 2.1: Port shaper-side `$lookup` array reads

**Files:** Modify `EntityProjectionExpression.cs`, `ObjectArrayProjectionExpression.cs`, `ObjectAccessExpression.cs`

- [ ] **Step 1: Port B's binding additions**

From B's diffs (`git diff d4aab4d..damieng/poc-include -- src/.../Expressions/EntityProjectionExpression.cs` etc.), port the branch that, for a cross-collection navigation, binds to the lookup alias:

```csharp
// EntityProjectionExpression.BindNavigation (cross-collection case):
var lookupAlias = $"_lookup_{navigation.Name}";
return navigation.IsCollection
    ? new ObjectArrayProjectionExpression(navigation, ParentAccessExpression, lookupAlias)
    : new ObjectAccessExpression(navigation, ParentAccessExpression, false, lookupAlias);
```

Port the matching constructor/field additions in `ObjectArrayProjectionExpression` and `ObjectAccessExpression` (the optional `lookupAlias`/element-name override).

- [ ] **Step 2: Build**

Expected: compiles.

- [ ] **Step 3: Commit**

```bash
git commit -am "EF-117: Port $lookup-array projection bindings (EntityProjection/ObjectAccess/ObjectArray)"
```

### Task 2.2: Route collection includes to `$lookup` + register the lookup

**Files:** Modify `MongoProjectionBindingExpressionVisitor.cs`, `MongoIncludeCompiler.cs`

- [ ] **Step 1: Write the failing MQL test in the spec suite**

In `tests/.../SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs`, change the `Include_collection` override from its current fan-out assertion to assert the `$lookup` pipeline:

```csharp
public override async Task Include_collection(bool async)
{
    await base.Include_collection(async);
    AssertMql(
        // single pipeline with a $lookup from Orders into "_lookup_Orders"
        // on the CustomerID/_id fields (exact baseline captured via rewriter)
        "Customers.{ \"$lookup\" : { \"from\" : \"Orders\", \"localField\" : \"_id\", \"foreignField\" : \"CustomerID\", \"as\" : \"_lookup_Orders\" } }");
}
```

Run: `dotnet test ...SpecificationTests... --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_collection"`
Expected: FAIL — current MQL shows separate fan-out `$match` queries, not a `$lookup`.

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL — current MQL shows no `$lookup` (fan-out issues separate queries).

- [ ] **Step 3: Enable the route in `ChooseStrategy`**

In `MongoIncludeCompiler.ChooseStrategy`, return `ServerLookup` for **embedded-false, collection, principal-side, single (non-nested)** includes:

```csharp
public static IncludeStrategy ChooseStrategy(IncludeExpression includeExpression, INavigation navigation)
{
    if (!IsCrossCollection(navigation))
        return IncludeStrategy.ClientFanOut;            // embedded: existing path

    // Stage 2: top-level collection include with no nested ThenInclude.
    if (navigation.IsCollection
        && !navigation.IsOnDependent
        && !HasNestedInclude(includeExpression.NavigationExpression))   // Stage 7 keeps these on fan-out
        return IncludeStrategy.ServerLookup;

    return IncludeStrategy.ClientFanOut;
}
```

Add `HasNestedInclude`, but per review R4 do **not** write a second tree-walker. Factor the existing walking logic out of `ExtractIncludeChainPath` into one private helper that lazily yields each nested `IncludeExpression`'s `INavigation` (e.g. `IEnumerable<INavigation> EnumerateNestedIncludes(Expression navigationExpression)`); `ExtractIncludeChainPath` joins their `Name`s and `HasNestedInclude` returns `.Any()`.

- [ ] **Step 4: Register the lookup + rewrite the shaper in the binding visitor**

In `MongoProjectionBindingExpressionVisitor`'s cross-collection `IncludeExpression` branch, dispatch on `ChooseStrategy`:
- `ServerLookup`: `_queryExpression.AddLookup(new LookupExpression(navigation));` then return B's `RewriteCollectionIncludeForLookup(includeExpression, navigation)` (port from `git show damieng/poc-include:src/.../MongoProjectionBindingExpressionVisitor.cs` lines ~560–660). This produces an `IncludeExpression` whose `CollectionShaperExpression` reads the `_lookup_<Nav>` array.
- `ClientFanOut`: keep A's current behavior (preserve the `IncludeExpression` for `MongoProjectionBindingRemovingExpressionVisitor.AddInclude` → `BuildCrossCollectionLoaderCall`).

- [ ] **Step 5: Run the failing test + spec regression**

Run the new test → Expected: PASS (MQL shows `$lookup`).
Run `--filter "FullyQualifiedName~NorthwindInclude"` → Expected: still ≥ floor (collection-include spec tests now pass via `$lookup`; nothing else regressed). Update any spec override whose MQL baseline changed from a fan-out `$match` to a `$lookup` (use `EF_TEST_REWRITE_BASELINES=1` **per class via `--filter`** — see `docs/EF-117-include-overview.md` discovery #6; never run the rewriter over the whole project twice).

- [ ] **Step 6: Commit**

```bash
git commit -am "EF-117: Route principal collection Include to server-side \$lookup; fan-out remains fallback"
```

### Task 2.3: Multi-EF check

- [ ] **Step 1: Run /test-all** to confirm EF8/EF9/EF10 all build and the Include suites pass.
- [ ] **Step 2: Commit any `#if EF8 || EF9` guards** needed (e.g. if B's code used EF10-only APIs).

---

## Stage 3 — Server-side reference include (`Order.Customer`)

**Goal:** route dependent→principal references to `$lookup` + `$unwind` and read the unwound object. This is where A's `IncludeJoinUnwrapper` (in the preprocessor) and B's `$lookup`+`$unwind` must be reconciled.

### Task 3.1: Decide the reference mechanism

**Files:** Modify `MongoQueryTranslationPreprocessor.cs`, `MongoIncludeCompiler.cs`

- [ ] **Step 1: Read both sides**
  - A: `git show EF-117a:src/.../MongoQueryTranslationPreprocessor.cs` — `IncludeJoinUnwrapper` rewrites EF's synthetic `Join`/`LeftJoin` + `Select(IncludeExpression)` into `Select(p => IncludeExpression(...))`.
  - B: the reference path strips the join (`StripJoinForLookup`) and emits `$lookup`+`$unwind`, **or** keeps the driver `LeftJoin` (`LeftJoinResult` `_outer`/`_inner`).
  - **Decision for this plan:** use B's `$lookup`+`$unwind` (simpler, no `_outer`/`_inner` reshaping for the single-reference case). Keep A's `IncludeJoinUnwrapper` so the `IncludeExpression` is surfaced uniformly, then register a `LookupExpression(navigation)` (whose `ShouldUnwind` is already true because `IsReference`).

- [ ] **Step 2: Verify the `IncludeJoinUnwrapper` interaction (review S5) — do this BEFORE writing the route**

A's `IncludeJoinUnwrapper` rewrites `Join/LeftJoin + Select(IncludeExpression(o.Outer, o.Inner, nav))` → `Select(p => IncludeExpression(p, default(TInner), nav))`, **dropping the inner entity** (the fan-out path materializes it via `LoadReference`, so the dropped sub-expression is unused). Confirm, by reading the pipeline order and adding a debug assertion, that:
  1. The unwrapper runs in the preprocessor **before** the `ServerLookup` classifier decides (it does — preprocessor precedes the projection-binding visitor), so the classifier sees a uniform `IncludeExpression`.
  2. The `LookupExpression` builder derives everything from `INavigation` **metadata** (FK/PK/collection), **not** from the now-`default(TInner)` `IncludeExpression.NavigationExpression` — so dropping the inner is harmless for the `$lookup` path (same separation `BuildCrossCollectionLoaderCall` already relies on).
  3. The shaper reads the `_lookup_<Nav>` alias, not the `default(TInner)` placeholder.

If any of these does not hold, gate the unwrapper to fire only for the fan-out shapes and let the `ServerLookup` path keep the original join shape. Record the finding inline in the plan.

- [ ] **Step 3: Write the failing MQL test** (spec suite, per R1)

Override `Include_reference` (and the self-join case, R8 below) in `NorthwindIncludeQueryMongoTest` to `await base...; AssertMql(...)` asserting a `$lookup` into `_lookup_Customer` followed by `$unwind` with `preserveNullAndEmptyArrays: true`. Run → FAIL (currently fan-out `FirstOrDefault` sub-query per order).

- [ ] **Step 4: Enable reference route**

Extend `ChooseStrategy`: for `navigation.IsOnDependent && !navigation.IsCollection` and single-FK (defer composite to Stage 5) → `ServerLookup`. In the binding visitor's `ServerLookup` branch, handle the reference shape: register the lookup and rewrite the shaper to read the unwound `_lookup_<Nav>` object (port B's reference read from `ObjectAccessExpression`/`MongoProjectionBindingRemovingExpressionVisitor`).

- [ ] **Step 5: Add a self-referencing navigation test (review R8)**

B handles self-joins (`Staff.Manager`) explicitly; A has no coverage. A self-reference is a `$lookup` on the *same* collection with `localField ≠ foreignField`, which the ported `LookupExpression` should produce without special-casing. Add a functional test (extend `IncludeTests.cs` with a `Manager`/`DirectReports` self-referencing model) asserting `Staff.Include(s => s.Manager)` materializes. Run → should PASS once Stage 3 routing is in; if it doesn't, the lookup builder is special-casing same-collection joins and needs a fix (compare against B's self-join branch).

- [ ] **Step 6: Run tests + spec regression**

Expected: reference + self-join tests PASS; `Include_reference*` spec tests pass via `$lookup`+`$unwind`; **full spec floor (0 failures) maintained**. Rebaseline affected MQL per-class (see Stage 8.2 for the per-class discipline).

- [ ] **Step 7: Commit**

```bash
git commit -am "EF-117: Route dependent reference + self-join Include to \$lookup + \$unwind"
```

---

## Stage 4 — Multi-level reference chains & reference+collection

**Goal:** support the families A currently rejects (`Include_reference_and_collection`, `Include_references_multi_level`, `Include_multiple_references_then_include_multi_level`) via chained `$lookup`(+`$unwind`).

> **This is the highest-risk stage** (review S4). B's `_outer`/`_inner` prefixing
> exists only because B keeps the driver's `LeftJoin` in the tree for multi-level
> chains; the prefix rule encodes which side of that driver-join a navigation's
> declaring type sits on. Before porting it, evaluate a simpler design.

### Task 4.0: Spike — can chained `$lookup`s avoid `_outer`/`_inner` entirely? (review S4)

**Files:** none (spike branch / throwaway).

- [ ] **Step 1: Prototype direct `$lookup` chaining** — each subsequent `$lookup` reads `from` the *previous* `$lookup`'s output field (or a `$unwind`ed sub-document path), with **no** intervening driver `LeftJoin` and therefore no `TransparentIdentifier`/`_outer`/`_inner` reshaping. Hand-build the pipeline for `Order.Include(o => o.Customer).ThenInclude(c => c.Orders)` against a throwaway collection and confirm the driver accepts it and the shaper can read the nested fields.
- [ ] **Step 2: Decide and record in the plan:**
  - If chained `$lookup`s work → adopt that model; **skip B's `_outer`/`_inner` port** (Task 4.1 below becomes "chain lookups in `AppendLookupStages` / field-path derivation"). Much simpler.
  - If not (driver/shaper can't address the nested path, or join semantics mismatch) → document *why* and fall back to porting B's `LeftJoin` + prefixing (Task 4.1 as written), **plus** add a sub-task to verify A's single-level `IncludeJoinUnwrapper` does not corrupt the multi-level driver-`LeftJoin` shapes B relies on (gate the unwrapper to the fan-out patterns only).

- [ ] **Step 3: Write up the spike outcome in `docs/EF-117-include-implementation-progress.md`** (new "Stage 4" section), **regardless of which direction wins** (reviewer request). That doc is the load-bearing record of "what we tried and why we picked this" for the include work — record the prototype, what the driver/shaper did, and the decision rationale.

### Task 4.1: Implement multi-level (per the Task 4.0 decision)

**Files:** Modify `MongoProjectionBindingExpressionVisitor.cs`, `MongoQueryExpression.cs`, possibly `MongoQueryTranslationPreprocessor.cs` (unwrapper gating)

- [ ] **Step 1: Write failing tests** — enable (un-reject) the relevant spec tests by overriding them to `await base.X(async); AssertMql(...)` with the chosen pipeline shape. Run → FAIL (currently `AssertTranslationFailed`).

- [ ] **Step 2: Implement the chosen design**
  - *Chained-`$lookup` path (preferred):* extend the field-path derivation so each level's `localField`/`from` references the prior level's output; no `UsesDriverJoinFields`.
  - *`LeftJoin` + prefixing path (fallback):* port B's prefixing from `git show damieng/poc-include:src/.../MongoProjectionBindingExpressionVisitor.cs` lines ~185–212 (when `UsesDriverJoinFields`, prefix `lookup.LocalField`/`lookup.As` with `_outer.`/`_inner.` per the navigation's declaring type), plus `ExtractNestedIncludePipeline`, **and** the unwrapper-gating sub-task from Task 4.0 Step 2.

- [ ] **Step 3: Extend `ChooseStrategy`** to return `ServerLookup` for reference-chain and reference+collection shapes (still excluding collection→collection, which Stage 7 keeps on fan-out).

- [ ] **Step 4: Run tests + full spec** — Expected: previously-rejected reference families now PASS; **full spec floor (0 failures) maintained**. Rebaseline MQL per class.

- [ ] **Step 5: Commit**

```bash
git commit -am "EF-117: Multi-level reference chains and reference+collection via chained \$lookup"
```

---

## Stage 5 — Composite-key `$lookup` (replace rejection)

**Goal:** make composite-key FK targets work via `_id.<field>` pathing (B's `GetFieldPath` already handles this) and remove A's composite-key `NotSupportedException` for the `$lookup` path (keep it only on the fan-out fallback until that path also supports composites).

### Task 5.1: Enable composite-key lookups

**Files:** Modify `MongoIncludeCompiler.cs` (`BuildCrossCollectionLoaderCall` composite guard), `ChooseStrategy`

- [ ] **Step 1: Write failing test** — a model with a composite-key dependent (mirror `OrderDetails` `_id.OrderID`/`_id.ProductID`); assert the `$lookup` uses `foreignField: "_id.OrderID"`. Run → FAIL (currently `NotSupportedException`).

- [ ] **Step 2: Route composite-key includes to `ServerLookup`**

In `ChooseStrategy`, drop the single-FK restriction for the `ServerLookup` branch (composite keys are fine for `$lookup` because `LookupExpression.GetFieldPath` already builds `_id.<field>`). Leave the composite-key `NotSupportedException` in `BuildCrossCollectionLoaderCall` so the **fan-out fallback** still rejects composites with the clear message.

- [ ] **Step 3: Run test + spec** — Expected: composite-key Include tests pass; floor maintained.

- [ ] **Step 4: Commit**

```bash
git commit -am "EF-117: Composite-key cross-collection Include via \$lookup _id pathing"
```

---

## Stage 6 — Filtered Include on BOTH paths

**Goal (explicit requirement):** filtered `Include` (`Where`/`OrderBy`/`Skip`/`Take` inside the navigation) works whether the include runs server-side (`$lookup` pipeline form) or client-side (fan-out). Implement both.

### Task 6.1: Server-side filtered include (pipeline `$lookup`)

**Files:** Modify `MongoProjectionBindingExpressionVisitor.cs` (`ExtractNestedIncludePipeline`)

- [ ] **Step 1: Write failing test** — `db.Customers.Include(c => c.Orders.Where(o => o.Total > 10).OrderBy(o => o.Date).Take(5))`; assert MQL uses the pipeline form of `$lookup` (`let` + `$match`/`$expr` + `$match`/`$sort`/`$limit`). Run → FAIL.

- [ ] **Step 2: Port B's pipeline extraction**

Port `ExtractNestedIncludePipeline` (B) so the navigation's filter/order/paging lambdas are translated to `BsonDocument` stages appended to `LookupExpression.PipelineStages`. `AppendLookupStages` already switches to the pipeline form when `HasPipeline`.

- [ ] **Step 3: Run test + spec** — Expected: server-side filtered include tests pass.

- [ ] **Step 4: Commit**

```bash
git commit -am "EF-117: Server-side filtered Include via pipeline \$lookup"
```

### Task 6.2: Client-side filtered include (fan-out)

**Files:** Modify `MongoIncludeCompiler.cs` (`ExtractIncludeChainPath` + `LoadCollection`)

- [ ] **Step 1: Write failing test** — force the fan-out path (a collection→collection `ThenInclude` with a filter on the inner collection, which Stage 7 keeps on fan-out) and assert the filter is applied (correct entities + ordering). Run → FAIL (fan-out currently ignores the nav filter).

- [ ] **Step 2: Capture filter/order/paging in the chain extractor**

Extend `ExtractIncludeChainPath` to also return the `Where`/`OrderBy`/`Skip`/`Take` lambdas found in the `Subquery` method chain alongside the `Select(o => fk == pk)` (the doc's "Follow-up: Filtered Include" note). Compose them onto the loader's `query` in `LoadCollection`:

```csharp
query = query.Where(predicate);
if (filterLambda is not null)   query = query.Where((Expression<Func<TRelated,bool>>)filterLambda);
if (orderByLambda is not null)  query = ApplyOrderings(query, orderings);
if (skip is not null)           query = query.Skip(skip.Value);
if (take is not null)           query = query.Take(take.Value);
return query.ToList();
```

- [ ] **Step 3: Run test + spec** — Expected: fan-out filtered include passes; floor maintained.

- [ ] **Step 4: Commit**

```bash
git commit -am "EF-117: Filtered Include support on the fan-out path"
```

---

## Stage 7 — Fan-out fallback routing (collection→collection ThenInclude, M2M)

**Goal:** make the fallback explicit and correct. Collection→collection `ThenInclude` and many-to-many stay on fan-out (B rejects the former; A already supports it). Ensure routing never sends an unsupported-by-`$lookup` shape to the server path.

### Task 7.1: Confirm fallback shapes route to fan-out

**Files:** Modify `MongoIncludeCompiler.ChooseStrategy`; tests extend `IncludeTests.cs` (materialization) + spec suites (MQL)

- [ ] **Step 1: Assert fallback MQL + materialization** — `db.Customers.Include(c => c.Orders).ThenInclude(o => o.Items)` (collection→collection) must NOT emit a `$lookup` for the inner collection; it must use fan-out (separate `$match` queries) and still materialize correctly. Run → should already PASS if `HasNestedInclude` correctly keeps these on fan-out (verifies Stage 2's guard). If it emits a broken `$lookup`, fix `HasNestedInclude`.

- [ ] **Step 2: M2M still rejected/fanned-out** — `Include(p => p.Tags)` asserts the existing `InvalidOperationException` ("many-to-many … not yet supported"). Confirm `ChooseStrategy` never returns `ServerLookup` for skip navigations (`includeExpression.Navigation is not INavigation`).

- [ ] **Step 3: Retain and test fan-out tracking propagation (review S2)** — `ApplyTrackingBehavior` in `MongoIncludeCompiler` is **fan-out-only** and must NOT be deleted as dead code: the `$lookup` path inherits the outer query's tracking mode implicitly (single materialization), but the fan-out fallback re-queries via a fresh `dbContext.Set<T>()` and still needs the explicit propagation. Add a test asserting `db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items)` (a fallback path) tracks nothing (`ChangeTracker.Entries()` empty). Add a one-line comment at each call site documenting "fan-out-only; `$lookup` inherits tracking implicitly."

- [ ] **Step 4: Resolve the unused-threading dead code (review R9)** — `BsonSerializerFactory` / `QueryContextParameter` were threaded into `MongoProjectionBindingRemovingExpressionVisitor` for the include path but never consumed by the fan-out loaders. Decide: (a) the `$lookup` shaper reads need them → wire them through and keep; or (b) they remain unused → delete the threading. Make the call explicit in the commit message.

- [ ] **Step 5: Run full spec suite + functional** — Expected: A's previously-passing collection→collection `ThenInclude` tests still pass (now explicitly via fan-out); B's previously-failing reference families pass (via `$lookup`); tracking test passes; **full spec floor (0 failures) maintained**. Net: union of both branches' coverage.

- [ ] **Step 6: Commit**

```bash
git commit -am "EF-117: Lock fan-out fallback (collection-chain ThenInclude, M2M); fan-out-only tracking; resolve dead threading"
```

---

## Stage 8 — Convention reconciliation, spec sweep, docs, multi-EF

**Goal:** decide the `MongoRelationshipDiscoveryConvention` question, run the full conformance sweep, update the ledger and docs, and validate all three EF versions.

### Task 8.1: Convention decision (gated)

**Files:** possibly `src/.../Metadata/Conventions/MongoRelationshipDiscoveryConvention.cs`

- [ ] **Step 1: Determine whether B's `ShouldBeOwnedType` change is needed (sharpened per review S3)**

"Does the spec suite pass" is necessary but **not sufficient** — the Northwind/EF fixtures configure relationships explicitly via fluent API (`HasMany().WithOne()`), so they never exercise *convention-discovered* cross-collection relationships, which is exactly what B's change affects. Run the richer check:
  1. Build a model with two entity types that each have a `DbSet` and a navigation between them, with **no** explicit relationship configuration. Save an entity and query it back.
  2. Without B's change: confirm the current behavior (the nav is convention-*embedded* into the principal's document — A's existing default).
  3. With B's change: the nav becomes a cross-collection relationship (separate documents).
  4. **Persisted-document-shape diff is the deciding factor**, not test pass/fail.

- [ ] **Step 2: Decide and document**
  - If no in-scope scenario relies on convention-discovered cross-collection relationships → **do not** port the change (keep the frozen default; note the limitation).
  - If a scenario needs it → port it, port B's `MongoRelationshipDiscoveryConventionTests`, and add a `BREAKING-CHANGES.md` entry classified as a **Behavior break (persisted document shape for convention-discovered navs)** per the AGENTS.md versioning rules — **not** merely an API break.

- [ ] **Step 3: Record the decision** (and the document-shape finding) in `docs/EF-117-include-overview.md` (new "Server-side `$lookup`" section), alongside the high-cardinality / 16 MB caveat from the rationale.

### Task 8.2: Full spec sweep + ledger

**Files:** `NorthwindInclude*QueryMongoTest.cs` (4 files) + every other suite whose Include MQL changed, `docs/failing-spec-tests.md`

> **Budget ~5× the apparent effort (review S6).** ~150 overrides were converted with
> captured MQL baselines during the staged EF-117 work; routing those shapes to
> `$lookup` invalidates **every** affected baseline. The EF baseline rewriter is
> **not idempotent across a whole-project run** (discovery #6 in
> `docs/EF-117-include-overview.md`: running it twice corrupted ~530 baselines and
> blew the suite runtime from ~35 s to 31 min). The only safe method is per-class
> with `--filter`, with a build between runs. This is a multi-hour, discipline-heavy
> task, not a single cleanup step.

- [ ] **Step 1: Write a rebaseline wrapper script** that runs `EF_TEST_REWRITE_BASELINES=1 dotnet test --filter "FullyQualifiedName~<Class>"` for each affected spec class **in sequence, one class at a time**, with a build between classes. Mirror the approach that worked in Stage 5 of the staged work. Never run the rewriter over the whole project.

- [ ] **Step 2 (one sub-commit per suite): rebaseline + flip overrides, class by class.** For each affected class: run the script, flip newly-supported overrides from `AssertTranslationFailed`/throw-asserting to `await base.X(async); AssertMql(...)`, verify the class is green, and commit that one class. Repeat. Affected classes are at least the 4 `NorthwindInclude*` plus any non-Include suite whose Include MQL changed (`NorthwindQueryFilters*`, `NorthwindQueryTagging*`, `NorthwindCompiled*`, `Mapping/BuiltInDataTypesMongoTest` — grep for `$match`-style Include baselines).

- [ ] **Step 3: Update `failing-spec-tests.md`** — reduce the EF-117 override count, describe the hybrid strategy, list the remaining genuine gaps (collection→collection via fan-out; M2M; set-operation includes; split query — still unimplemented).

- [ ] **Step 4: Full-project regression on all three EF targets** — `/test-all`, expect 0 failures everywhere. Commit.

```bash
git commit -am "EF-117: Spec sweep + ledger update for hybrid \$lookup/fan-out Include"
```

### Task 8.3: Multi-EF + provider self-review

- [ ] **Step 1: Run /test-all** (EF8/EF9/EF10 build + full test). Fix `#if EF8 || EF9` / `#if !EF8` guards for any EF10-only driver APIs B used (e.g. `LeftJoin` queryable method exists only `#if !EF8 && !EF9`).
- [ ] **Step 2: Run `/review-ef-core-provider`** on the branch — address `query-reviewer`, `ef-conformance-reviewer`, and `api-stability-reviewer` findings (especially any annotation-key/behavior changes from the convention decision).
- [ ] **Step 3: Final commit + open PR** titled `EF-117: Hybrid server-side \$lookup Include with fan-out fallback`.

---

## Stage 9 (OPTIONAL / follow-up, out of the requested scope) — explicit `AsSplitQuery` / `AsSingleQuery`

**Status:** Not part of this deliverable. The user chose **automatic** hybrid routing
(`$lookup` default, fan-out fallback), so this plan implements `ChooseStrategy` as an
automatic decision. Review S7 correctly notes that the centralized `ChooseStrategy`
hook is *also* the natural place to later expose a user-facing override — `$lookup`
≈ "single query", fan-out ≈ "split query" — and that the high-cardinality / 16 MB
caveat (see rationale) is the motivating use case for an escape hatch.

If pursued later: map EF Core's `AsSplitQuery()` → force `ClientFanOut` and
`AsSingleQuery()` → force `ServerLookup` (where the shape supports it), read the
`QuerySplittingBehavior` from `QueryCompilationContext`, and have `ChooseStrategy`
honor an explicit override before falling back to its automatic decision. This is
small once both paths and the routing are in place — but it is a separate ticket,
explicitly **not** built here.

---

## Self-review (run before handing off)

**1. Spec coverage vs the requirements gathered:**
- Hybrid ($lookup default + fan-out fallback) → Stages 2–4 (server default) + Stage 7 (fallback). ✅
- Reference via LeftJoin/$lookup+$unwind → Stage 3. ✅
- Multi-level reference chains → Stage 4. ✅
- Filtered Include (pipeline $lookup) → Stage 6.1. ✅
- Composite-key $lookup (_id pathing) → Stage 5. ✅
- **Filtering on BOTH client and server paths** (explicit ask) → Stage 6.1 (server) + Stage 6.2 (fan-out). ✅
- Collection→collection ThenInclude + M2M fallback preserved → Stage 7. ✅

**2. Placeholder scan:** Code-bearing steps either include the literal code (`LookupExpression` port, `PendingLookups`, BSON emission, `ChooseStrategy`, fan-out filter composition) or give an exact B source location to port (`git show damieng/poc-include:<file>` + line range + symbol). The large expression-tree rewrites (`RewriteCollectionIncludeForLookup`, `ExtractNestedIncludePipeline`, `_outer`/`_inner` prefixing) are deliberately specified as **ports of named, line-located B symbols** rather than re-typed inline — this is a port across two real forks, and inventing "complete" replacements would be fabrication. Flagged explicitly so the executor reads B, not guesses.

**3. Type consistency:** `IncludeStrategy` (enum, no `Default` constant — R2), `ChooseStrategy`, `HasNestedInclude` / `EnumerateNestedIncludes`, `LookupExpression`, `PendingLookups`/`AddLookup`/`UsesDriverJoinFields`, `AppendLookupStages`, `_lookup_<Nav>` alias used consistently across stages.

**Known risk to call out at review time:** the multi-level handling (Stage 4) is the most fragile part. It is now de-risked by the **Task 4.0 spike**, which evaluates direct chained `$lookup`s (no driver `LeftJoin`, no `_outer`/`_inner` prefixing) before committing to B's port. If the spike fails, the fallback port includes an explicit sub-task to gate A's single-level `IncludeJoinUnwrapper` away from the multi-level driver-`LeftJoin` shapes. Budget extra review here regardless.

**Review incorporation (2026-05-30):** this plan was revised after the EF-117-author review (`2026-05-30-lookup-include-hybrid.review.md`). Blocking B1–B3 fixed (branch `EF-117c` off `EF-117b`; full-suite/3-EF floor; dropped the impractical translator unit-MQL test, kept a feasible `LookupExpression` unit test). Substantive S1–S8 and refinements R1–R10 incorporated, except **S7 (AsSplitQuery)** which is deliberately scoped out as optional Stage 9 (the user chose automatic routing). See the response doc `2026-05-30-lookup-include-hybrid.response.md` for the item-by-item disposition.

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-30-lookup-include-hybrid.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session with checkpoints for review.

Note: execution must happen in `~/code/mongo-efcore-provider` on the new `EF-117c` branch (off `EF-117b`, where A lives), **not** in this `provider2` clone.
