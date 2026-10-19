# Navigation-less join native eligibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `NorthwindKeylessEntitiesQueryMongoTest.Entity_mapped_to_view_on_right_side_of_join` (and the
general shape it represents — a `Join`/`LeftJoin`/`GroupJoin`+`SelectMany(DefaultIfEmpty)` between two entity
types with **no model navigation connecting them**) go native instead of falling back to driver-LINQ. The
underlying eligibility widening is version-independent, but the target test itself ended up EF10-only in
practice, because EF8/EF9 never reach the widened code for that exact query shape (a pre-existing, unrelated
EF8/EF9 nav-expansion limitation, documented in the Task 5 commit).

**Architecture:** `TranslateJoinCore`'s native-eligibility gate (`JoinInfo.IsNativelyEligible`) currently
requires a resolved `Navigation`. The bookkeeping it would need for a navigation-less join already exists —
`RebindInnerShaperToOuterQuery`'s EF-377 "bare key-equality Join hop" branch already builds a correct
`joinInfo.Lookup` straight from the key-selector property names when no navigation resolves, and
`MongoJoinScope`/`NativeJoinScopeProjectionBinder` never reference `Navigation` at all. So this is a **2-hunk
gate widening**, not a new mechanism: drop the `Navigation != null` requirement from `IsNativelyEligible`, and
teach `JoinLookupImplementsKeySelectors` to trust the already-correctly-built `Lookup` when there's no
navigation to re-derive an anchor entity type from. Confirmed by a manual spike before writing this plan
(see the spec's "Confirmed by spike" section) — the target test goes native and returns correct results with
exactly this change, and no other file needs to change for the mechanism itself.

**Tech Stack:** C#/.NET, EF Core provider internals (`src/MongoDB.EntityFrameworkCore/Query/`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-22-navigationless-join-native-eligibility-design.md`

## Global Constraints

- Multi-EF targeting: code under `src/` must build clean under EF8/EF9/EF10 (`Debug EF8`/`EF9`/`EF10`
  configurations). Nothing in this plan touches an EF-version-conditional API — verify in Task 5.
- `MONGODB_EF_NATIVE_ONLY=1` is the only reliable "did this actually go native" signal — MQL-shape assertions
  under default `Native` mode do not distinguish native from a structurally-identical fallback pipeline (see
  `Query/AGENTS.md`, Common Pitfalls). Every "goes native" claim in this plan must be backed by a
  `MongoQueryMode.NativeOnly` assertion, not a log/MQL string alone.
- A fallback → native transition with unchanged results is **not** a breaking change and MQL shape is **not**
  contract (per `AGENTS.md`) — baseline updates in Task 4 are expected and correct, not regressions, as long
  as the underlying query results are unchanged.
- Do **not** touch `NativeJoinTests.cs`'s existing
  `Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly` test or its fixture
  (`Owner.Region`/`Order.Region`, both types DO have a navigation) — despite its name, it pins a *different*,
  still-valid decline (a navigation resolved to the *wrong* target via `RebindInnerShaperToOuterQuery`'s loose
  `FirstOrDefault` fallback, caught by `JoinLookupImplementsKeySelectors`'s LocalField/ForeignField comparison
  in the `Navigation != null` branch, which this plan does not change). Confirm in Task 1 that it still passes
  unmodified — that is the regression guard proving the two cases stay distinct.

---

## File structure

- Modify `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  — `TranslateJoinCore`'s `IsNativelyEligible` assignment (~line 2440) and `JoinLookupImplementsKeySelectors`
  (~line 2498-2547). No other source file changes.
- Modify `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — add a new depth-1
  genuinely-navigation-less native-goes-native test, alongside the existing (unrelated, unchanged)
  `Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly`.
- Modify `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NavigationlessJoinChainTests.cs` — add
  `MongoQueryMode.NativeOnly` assertions proving the existing bare-hop chain scenarios (already correct via
  fallback) now go native, and update the file's doc comment (it currently frames EF-377 purely as a
  fallback-correctness fix).
- Modify `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindKeylessEntitiesQueryMongoTest.cs`
  — `Entity_mapped_to_view_on_right_side_of_join`'s comment and `AssertMql` baseline.
- Modify (baseline-only) any other spec test whose MQL shape flips as a side effect — confirmed by the spike
  to include at least `NorthwindJoinQueryMongoTest.Join_same_collection_force_alias_uniquefication` and
  `.GroupJoin_customers_employees_shadow`; Task 4 re-measures the full set.
- Modify `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — note under "Durable invariants" that native
  join-scope eligibility no longer requires a resolved navigation.

## Interfaces

- **Consumes:** `JoinInfo.Navigation` (nullable `INavigation?`, existing), `JoinInfo.Lookup` (nullable
  `LookupExpression?`, existing, already built for the navigation-less case by
  `RebindInnerShaperToOuterQuery`), `MongoSelectDefinition.JoinScope`/`MongoJoinScope`/`MongoJoinScopeLevel`
  (existing, no `Navigation` dependency).
- **Produces:** `JoinInfo.IsNativelyEligible` now `true` for a navigation-less join whose key selectors
  resolved to real properties on both sides (unchanged signature/type — still `bool`, set at the same call
  site).

---

### Task 1: Widen native eligibility for navigation-less joins

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:2440-2446` (`IsNativelyEligible`), `:2504-2529` (`JoinLookupImplementsKeySelectors`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `JoinInfo.Navigation`, `JoinInfo.Lookup`, `JoinInfo.IsLeftOuter`, `MongoSelectDefinition.IsBareCollectionScan`/`IsGroupBy`/`IsDistinct`.
- Produces: `JoinInfo.IsNativelyEligible == true` for a bare key-equality join with no model navigation.

- [ ] **Step 1: Write the failing functional test**

Add to `NativeJoinTests.cs`, immediately after `Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly` (~line 840), reusing the existing `Owner`/`OrderLine` types, which have **no navigation between them at all** (only `Order` navigates to each):

```csharp
[Fact]
public void Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly()
{
    // Unlike Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly above (which pins a
    // WRONGLY-resolved navigation between two types that DO have one), Owner and OrderLine have NO
    // navigation connecting them at all — this is the genuinely-navigation-less case.
    var seed = SeedOwnersOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly));

    var results =
        db.Owners
            .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o, ol })
            .ToList();

    Assert.Empty(results); // No seeded Region/Sku values match — this only proves the query TRANSLATES
                            // and EXECUTES natively (no NativeTranslationNotSupportedException), not a
                            // row-count claim. Row-shape correctness is Task 2's job.
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly"`
Expected: FAIL with `NativeTranslationNotSupportedException` (query still falls back today).

- [ ] **Step 3: Apply the minimal source change**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, replace:

```csharp
        joinInfo.IsNativelyEligible =
            joinInfo.Navigation is { } eligibleNavigation
            && innerQueryExpression.Select.IsBareCollectionScan
            && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
            && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
            && !(joinInfo.IsLeftOuter && eligibleNavigation.IsCollection)
            && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);
```

with:

```csharp
        joinInfo.IsNativelyEligible =
            innerQueryExpression.Select.IsBareCollectionScan
            && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
            && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
            && !(joinInfo.IsLeftOuter && joinInfo.Navigation is { IsCollection: true })
            && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);
```

Then in `JoinLookupImplementsKeySelectors`, replace:

```csharp
        var outerAnchorEntityType = joinInfo.Navigation!.DeclaringEntityType;
        var outerProperty = outerAnchorEntityType.FindProperty(outerKeyName);
        var innerProperty = joinInfo.InnerEntityType.FindProperty(innerKeyName);
        if (outerProperty == null || innerProperty == null)
        {
            return false;
        }
```

with:

```csharp
        if (joinInfo.Navigation == null)
        {
            // No navigation to re-derive an anchor entity type from. joinInfo.Lookup (checked above) was
            // already built DIRECTLY from these same outerKeySelector/innerKeySelector property names by
            // RebindInnerShaperToOuterQuery's EF-377 raw-key branch, so — unlike the navigation branch below,
            // which guards against a navigation resolved to the WRONG target — there is nothing to
            // re-verify: the Lookup already implements exactly what the selectors say by construction.
            return true;
        }

        var outerAnchorEntityType = joinInfo.Navigation!.DeclaringEntityType;
        var outerProperty = outerAnchorEntityType.FindProperty(outerKeyName);
        var innerProperty = joinInfo.InnerEntityType.FindProperty(innerKeyName);
        if (outerProperty == null || innerProperty == null)
        {
            return false;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly"`
Expected: PASS.

- [ ] **Step 5: Confirm the existing wrongly-resolved-navigation decline test still passes unmodified**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly"`
Expected: PASS, unchanged — this is the regression guard that the two cases (no navigation vs. wrongly-resolved navigation) stay distinct.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-322: admit navigation-less key-equality joins to native join-scope eligibility"
```

---

### Task 2: Differential-correctness coverage for the navigation-less join family

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `SeedOwnersOrdersAndLines()` (existing), `CreateContext(Seed, MongoQueryMode, string)` (existing).
- Produces: nothing new consumed elsewhere — a standalone regression test.

Existing tests in this file already differential-test the navigation-*backed* join family against an
in-memory LINQ oracle (see `Join_result_matches_in_memory_oracle_including_unmatched_rows`). This task adds
the matched/unmatched/left-null differential coverage Task 1's test deliberately skipped (it only proved
"translates and executes without throwing", not "returns the same rows the oracle would").

- [ ] **Step 1: Write the failing test**

Add to `NativeJoinTests.cs`:

```csharp
[Theory]
[InlineData(MongoQueryMode.Native)]
[InlineData(MongoQueryMode.DriverLinq)]
public void Genuinely_navigation_less_join_matches_in_memory_oracle_including_unmatched_rows(MongoQueryMode mode)
{
    var owners = new[]
    {
        new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "MATCH" },
        new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "NOMATCH" },
    };
    var orderLines = new[]
    {
        new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = ObjectId.GenerateNewId(), Sku = "MATCH", Quantity = 1 },
    };
    var seed = new Seed(owners, [], orderLines);

    using var db = CreateContext(seed, mode,
        nameof(Genuinely_navigation_less_join_matches_in_memory_oracle_including_unmatched_rows) + mode);

    var actual = db.Owners
        .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol.Sku })
        .ToList();

    var expected = owners
        .Join(orderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol.Sku })
        .ToList();

    Assert.Equal(expected.Count, actual.Count);
    Assert.Equal(
        expected.Select(e => (e.OwnerName, e.Sku)).OrderBy(x => x.OwnerName),
        actual.Select(a => (a.OwnerName, a.Sku)).OrderBy(x => x.OwnerName));
}
```

- [ ] **Step 2: Run test to verify it fails (or passes for the wrong reason)**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Genuinely_navigation_less_join_matches_in_memory_oracle_including_unmatched_rows"`
Expected: PASS already (Task 1's change is in place and this only exercises `Native`/`DriverLinq`, both of
which worked before Task 1 too via fallback) — this test's job is to be a **standing regression guard**, not
to newly fail. Confirm it passes under both modes; if `Native` mode's row content ever silently diverges from
`DriverLinq`'s in a future change, this is what catches it.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: pin navigation-less join native/fallback result parity"
```

---

### Task 3: Prove chained navigation-less joins also go native

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NavigationlessJoinChainTests.cs`

**Interfaces:**
- Consumes: `NavigationlessJoinDbContext` (existing, parametrized by `firstHopHasNavigation`/`secondHopHasNavigation`).
- Produces: nothing new consumed elsewhere.

The existing four facts in this file only prove **correct results**, not **native execution** — they run
in default `Native` mode, where a silently-falling-back query still passes. Task 1's change means these
chains are now eligible to go native too (per the spec's "Confirmed by spike" — chain-level eligibility
already has no `Navigation` dependency once depth-1 does). This task proves it explicitly and updates the
file's own doc comment, which currently frames EF-377 as purely a fallback-correctness fix.

- [ ] **Step 1: Write the failing test**

Add to `NavigationlessJoinChainTests.cs`:

```csharp
[Fact]
public void Both_hops_bare_key_equality_joins_go_native_under_NativeOnly()
{
    var (rootsName, midsName, leavesName) = Seed();

    using var db = new NavigationlessJoinDbContext(
        database, rootsName, midsName, leavesName, secondHopHasNavigation: false, queryMode: MongoQueryMode.NativeOnly);

    var results =
        db.NRoots
            .Join(db.NMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
            .Join(db.NLeaves, x => x.m.LeafId, l => l.Id, (x, l) => new { x.r, l })
            .ToList();

    var result = Assert.Single(results);
    Assert.Equal("R1", result.r.Name);
    Assert.Equal("A", result.l.Label);
}
```

This requires threading a `MongoQueryMode` through `NavigationlessJoinDbContext`'s constructor (currently
hardcoded to whatever `UseMongoDB` defaults to). `UseMongoDB` itself has no query-mode parameter — the
established pattern in this test project (see `NativeJoinTests.cs`'s own `JoinTestDbContext.BuildOptions`) is
a separate `new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode)` statement applied to the
already-built `DbContextOptionsBuilder`. Update the constructor to match that exact pattern (this needs
converting from the primary-constructor/expression-bodied `: DbContext(...)` form to an explicit constructor
body, since `UseQueryMode` must run as a statement, not inside a single chained expression):

```csharp
class NavigationlessJoinDbContext : DbContext
{
    public DbSet<NRoot> NRoots { get; set; }
    public DbSet<NMid> NMids { get; set; }
    public DbSet<NLeaf> NLeaves { get; set; }

    private readonly bool _firstHopHasNavigation;
    private readonly bool _secondHopHasNavigation;

    public NavigationlessJoinDbContext(
        TemporaryDatabaseFixture database, string rootsCollection, string midsCollection, string leavesCollection,
        Action<string>? logAction = null, bool firstHopHasNavigation = false, bool secondHopHasNavigation = true,
        MongoQueryMode queryMode = MongoQueryMode.Native)
        : base(BuildOptions(database, logAction, queryMode))
    {
        _firstHopHasNavigation = firstHopHasNavigation;
        _secondHopHasNavigation = secondHopHasNavigation;
        RootsCollection = rootsCollection;
        MidsCollection = midsCollection;
        LeavesCollection = leavesCollection;
    }

    // NOTE for the implementer: this sketch only shows the query-mode plumbing change. Port every field this
    // class's ORIGINAL primary-constructor body captured (rootsCollection/midsCollection/leavesCollection for
    // OnModelCreating's ToCollection calls) into equivalent private fields the same way — read the actual
    // current file before editing, do not reconstruct it from this snippet alone.

    private static DbContextOptions BuildOptions(
        TemporaryDatabaseFixture database, Action<string>? logAction, MongoQueryMode queryMode)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NavigationlessJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .LogTo(l => logAction?.Invoke(l))
            .EnableSensitiveDataLogging();
        new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(queryMode);
        return optionsBuilder.Options;
    }
}
```

Adapt field/property names to whatever the current file actually uses for `OnModelCreating`'s collection-name
capture — read the file first; do not assume the sketch above is a verbatim drop-in for every unrelated
member.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Both_hops_bare_key_equality_joins_go_native_under_NativeOnly"`
Expected: FAIL before Task 1's change is applied elsewhere in the branch — but since Task 1 already landed
earlier in this plan, expected here is actually PASS. If it still fails, that means chain-level eligibility
has its own residual gap Task 1 didn't cover — STOP, do not loosen anything further in this task; instead
open a follow-up ticket documenting the gap (per the spec's "Out of scope" list) and skip to Step 4 with the
test marked `[Fact(Skip = "...")]` referencing that ticket.

- [ ] **Step 3: If it passed, add the analogous test for the other two existing scenarios**

Repeat for `Bare_first_hop_then_navigation_backed_second_hop` and
`Navigation_backed_first_hop_then_bare_second_hop`, each asserting under `MongoQueryMode.NativeOnly`.

- [ ] **Step 4: Update the file's doc comment**

Amend the class-level `<summary>` to note that these hops are now native-eligible, not just
fallback-correct, referencing this plan's ticket number.

- [ ] **Step 5: Run the full file**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NavigationlessJoinChainTests"`
Expected: all PASS (the pre-existing default-mode facts unaffected).

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NavigationlessJoinChainTests.cs
git commit -m "test: prove navigation-less join chains go native under NativeOnly"
```

---

### Task 4: Regenerate affected spec-suite MQL baselines

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindKeylessEntitiesQueryMongoTest.cs`
- Modify (baseline only, no logic changes): any other `*MongoTest.cs` file under
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/` whose baseline flips. Confirmed by the spike:
  `NorthwindJoinQueryMongoTest.cs` (this single file contains both `Join_same_collection_force_alias_uniquefication`
  and `GroupJoin_customers_employees_shadow` — there is no separate `NorthwindGroupJoinQueryMongoTest.cs` in
  this codebase). Other files may also be affected; Step 1-2 below re-measure the full set.

**Interfaces:**
- Consumes: `EF_TEST_REWRITE_BASELINES=1` env var (existing, see SpecificationTests `AGENTS.md`),
  `MONGODB_EF_NATIVE_ONLY=1` env var (existing).

- [ ] **Step 1: Run the full spec suite under NativeOnly to find every newly-native (previously-declining) case**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpecificationTests.Query" 2>&1 | tee /tmp/native-only-after.txt`

- [ ] **Step 2: Run the full spec suite under default Native mode to find every MQL baseline mismatch**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpecificationTests.Query" 2>&1 | tee /tmp/native-baseline-mismatches.txt`
Collect every `FAIL`ing test name into a list — these are candidates for baseline regeneration, but each
must be individually reviewed in Step 3 before blindly rewriting (a mismatch could be a genuine regression,
not just a shape flip).

- [ ] **Step 3: For each failing test, manually diff expected vs. actual MQL**

For each test from Step 2, read the `Assert.Equal` failure's expected/actual strings. Confirm each is a
**fallback-shape → native-shape** flip (the `_outer`/`_inner` driver-bridge document shape replaced by a
flat `_lookup_<X>`/`$unwind` native shape, or vice versa is NOT expected) with the **same** logical join
condition (`localField`/`foreignField` pair) and left/inner-ness. Any diff that is NOT purely this shape
change (e.g., a changed field name, a dropped stage, a different row-affecting operator) is a genuine
regression — STOP and investigate via `superpowers:systematic-debugging` before proceeding, do not rewrite
the baseline over it.

- [ ] **Step 4: Regenerate the confirmed-safe baselines**

Run: `EF_TEST_REWRITE_BASELINES=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~<EachConfirmedTestName>"` for each confirmed test (or the whole
`NorthwindJoinQueryMongoTest`/`NorthwindKeylessEntitiesQueryMongoTest`/etc. classes at once if every failure
in them was confirmed safe in Step 3).

- [ ] **Step 5: Update `Entity_mapped_to_view_on_right_side_of_join`'s stale comment**

In `NorthwindKeylessEntitiesQueryMongoTest.cs`, remove the `// Failed: Throws ExpressionNotSupportedException
(query not translated)` comment above the `await base.Entity_mapped_to_view_on_right_side_of_join(async);`
call — it no longer fails.

- [ ] **Step 6: Run the full spec suite once more to confirm everything is green**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpecificationTests.Query"`
Expected: 0 failures.

- [ ] **Step 7: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
git commit -m "test: regenerate MQL baselines flipped fallback-to-native by navigation-less join eligibility"
```

---

### Task 5: Multi-EF-version verification and documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: none new.
- Produces: none new — documentation only.

- [ ] **Step 1: Run the full suite under all three EF configurations**

Run (or invoke the `/test-all` skill, which does this in parallel):
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
(Build each configuration first: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"` / `EF9` / `EF10`.)
Expected: 0 failures on all three. Note `NavigationlessJoinChainTests`'s existing
`Bare_first_hop_then_GroupJoin_left_join_pattern_returns_correct_rows` is already `#if !EF8 && !EF9` — no new
guard expected from this plan's changes, since `TranslateJoinCore` is not itself EF-version-conditional, but
confirm this assumption holds rather than skipping the EF8/EF9 runs.

- [ ] **Step 2: Add a durable-invariants note**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, under "Durable invariants", add one bullet near the
existing chained-join-scope entries:

```markdown
- **Native join-scope eligibility does not require a resolved navigation.** A bare key-equality
  `Join`/`LeftJoin`/`GroupJoin` with no model navigation connecting the two sides is native-eligible exactly
  like a navigation-backed one, as long as `RebindInnerShaperToOuterQuery`'s EF-377 raw-key branch resolved
  both key properties (`JoinInfo.Lookup != null`). Don't confuse this with a navigation that resolves to the
  WRONG target (`RebindInnerShaperToOuterQuery`'s loose `FirstOrDefault` fallback) — that's a DIFFERENT,
  still-declining case guarded by `JoinLookupImplementsKeySelectors`'s LocalField/ForeignField comparison in
  the navigation-present branch. See `NativeJoinTests.cs`'s
  `Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly` (misleadingly named — pins the
  wrong-navigation case) vs. `Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly` (the
  true no-navigation case) for the worked distinction.
```

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "docs: note navigation-less join native eligibility in Query AGENTS.md"
```

---

## Self-review notes

- **Spec coverage:** Task 1 covers the design's core change; Task 2 covers the differential-correctness
  requirement; Task 3 covers the chain-eligibility extension and its own explicit "if it doesn't already
  work, stop and file a follow-up rather than widen further" branch (spec's "Out" list — chain `Where`/
  scalar-leaf residual `Navigation` dependencies are NOT silently expanded into scope here); Task 4 covers
  baseline regeneration and the manual-diff safety check the spec calls for; Task 5 covers multi-EF
  verification and documentation.
- **Placeholder scan:** no TBD/TODO steps; every code step has actual diffs; Task 4's "confirmed-safe"
  language is a real judgment step (with an explicit stop condition), not a placeholder.
- **Type/name consistency:** `JoinInfo.IsNativelyEligible`, `JoinInfo.Navigation`, `JoinInfo.Lookup`, and
  `JoinLookupImplementsKeySelectors` are used identically across Task 1's before/after diffs and the spec.
  Task 3's `NavigationlessJoinDbContext` constructor change is additive (new optional parameter with a
  default), so it does not break the three pre-existing call sites in that file.
