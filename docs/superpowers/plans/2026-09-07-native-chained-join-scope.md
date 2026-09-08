# Native chained-join scope (Where/OrderBy/whole-entity-leaves projection/terminal) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any` (and the general shape it
represents — a chain of 2+ `Join`/`LeftJoin` calls, `Where`/`OrderBy`/`ThenBy` predicates and keys that only
touch the outermost root scope, and a trailing whole-entity-leaves-only projection (implicit or explicit)
terminating in a reducer or scalar aggregate) go native instead of falling back to driver-LINQ.

**Architecture:** Generalize the existing single-join native-join-scope mechanism from exactly one join to an
ordered chain, in two independently-testable layers that mirror the existing depth-1 split: (1)
`MongoJoinScope` **metadata** (which entity types/aliases exist at each level) is built eagerly at
join-registration time for every join, exactly as depth-1 already does for its one join — this is what lets
`Where`/`OrderBy` resolve against the chain even though they run before any confirming `Select`; (2) **join
confirmation** (registering the `$lookup`, flipping `Route` away from `Fallback`) stays deferred to the
Select-side arms exactly as today, with `IsSingleEligibleNativeJoinScope` and
`NativeJoinScopeProjectionBinder` widened from "exactly one join" to "every join in the chain". Reuse the
already-generic `MongoTransparentScopeResolver` (built for `SelectMany`'s chained scopes) both for `Where`/
`OrderBy` (restricted to the outermost root scope only) and for the projection binder's per-leaf recognition
(any scope index). Add the missing `OrderBy`/`ThenBy` join-scope arm to `NativeSlotPopulator` (currently
absent even for a single join).

**Tech Stack:** C#/.NET, EF Core provider internals (`src/MongoDB.EntityFrameworkCore/Query/`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-07-native-chained-join-scope-design.md`

## Global Constraints

- Multi-EF targeting: code under `src/` must build clean under EF8/EF9/EF10 (`Debug EF8`/`EF9`/`EF10`
  configurations). Nothing in this plan touches an EF-version-conditional API, so no new `#if` guards are
  expected — verify this assumption in Task 9.
- Scope resolution must always be by parameter identity (`ReferenceEquals`), never by member name — see
  `Query/AGENTS.md`'s durable invariant.
- A gate that decides join-chain eligibility must call the *same* structural predicate
  (`JoinLookupImplementsKeySelectors`, navigation/left-outer/collection checks) the existing depth-1 arms
  already use — never a looser restatement.
- Do not weaken or bypass the existing `Where`-arm Outer-only restriction for depth-1 joins
  (`NativeJoinScopeTranslator.ReferencesInnerScope`) — this plan only extends that same restriction across a
  chain, never widens it to admit Inner access.
- `MONGODB_EF_NATIVE_ONLY=1` is the only reliable "did this actually go native" signal — MQL-shape assertions
  under default `Native` mode do not distinguish native from a structurally-identical fallback pipeline (see
  `Query/AGENTS.md`, Common Pitfalls).

---

## File structure

- Modify `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs` — flat single-join record →
  chain-capable `MongoJoinScope` (root entity type + ordered `MongoJoinScopeLevel[]`).
- Modify `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — no field-shape
  change (still one nullable `JoinScope` property), only doc-comment updates reflecting the new chain
  semantics.
- Modify `src/MongoDB.EntityFrameworkCore/Query/Expressions/JoinInfo.cs` — add an `IsNativelyEligible` bool,
  set once per join at registration time (Task 3).
- Modify `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  — `TranslateJoinCore`'s eligibility computation (lines 2213-2223) generalized to run per-join and
  (re)build a chain-covering `JoinScope` (Task 3, metadata only — no confirmation there);
  `IsSingleEligibleNativeJoinScope` widened from `Joins.Count == 1` to "every join in `Joins` is covered by
  `JoinScope`" (Task 6, confirmation-side).
- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs` — add
  `TryTranslateRootScopeOnly` (predicate and value mode) built on `MongoTransparentScopeResolver`; existing
  entry points unchanged (Task 4).
- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` — generalize the
  `Where` join-scope arm to branch on chain depth; add the missing `OrderBy`/`OrderByDescending`/`ThenBy`/
  `ThenByDescending` join-scope arm (Task 5).
- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs` — per-leaf
  recognition generalized from the flat Outer/Inner check to `MongoTransparentScopeResolver`-based scope-index
  resolution, admitting a whole-entity leaf at any level of the chain (Task 6).
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs`
  — new cases for `TryTranslateRootScopeOnly`.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — new end-to-end
  `NativeOnly` cases for the chained shape.
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs`
  — rebaseline `Multiple_joins_Where_Order_Any`'s `AssertMql(...)`.

---

### Task 1: Pin current behavior with a failing `NativeOnly` test

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `MongoQueryMode.NativeOnly`, `CreateContext(seed, mode, dbName)`, `SeedOwnersAndOrders()` (or
  the closest existing three-entity seed helper in this file — if none exists with three joinable
  collections, extend the seed helper in this same task; see Step 1).
- Produces: a red test (`Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly`) that the rest of this
  plan turns green — nothing downstream depends on new production symbols yet.

- [ ] **Step 1: Confirm/extend the seed fixture**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` and check whether the
existing seed (`SeedOwnersAndOrders` or similar) has a third, joinable collection (e.g. an `OrderLine`/`Item`
entity keyed off `Order`). If not, add one to the seed helper — a plain entity with an `OrderId`-style FK to
the existing `Order` entity is enough; no new navigation is required if the model resolves the join via a
raw key-equality (EF-377 already covers navigation-less joins).

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public void Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly()
{
    var seed = SeedOwnersAndOrdersAndLines(); // from Step 1
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly));

    var found = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
        .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
        .Where(x => x.o.Name == seed.Owners[0].Name)
        .OrderBy(x => x.o.Id)
        .Any();

    Assert.True(found);
}
```

- [ ] **Step 3: Run it and confirm it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly"`

Expected: FAIL with `NativeTranslationNotSupportedException`. This confirms the plan is starting from the
documented red state, not a state that already accidentally passes.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: pin chained-join Where/OrderBy/Any as currently falling back to driver-LINQ"
```

---

### Task 2: `MongoJoinScope` becomes chain-capable

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs:558-560` (doc comment only)

**Interfaces:**
- Consumes: nothing new.
- Produces: `MongoJoinScope(IEntityType outerEntityType, IReadOnlyList<MongoJoinScopeLevel> levels)` with
  `.OuterEntityType`/`.Levels`; `MongoJoinScopeLevel(IEntityType innerEntityType, string innerPrefix, bool
  isLeftOuter)` with `.InnerEntityType`/`.InnerPrefix`/`.IsLeftOuter`. Every later task reads `scope.Levels[0]`
  in place of today's flat `scope.InnerEntityType`/`scope.InnerPrefix`/`scope.IsLeftOuter`.

- [ ] **Step 1: Rewrite `MongoJoinScope.cs`**

```csharp
namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Records that a native <c>Join</c>/<c>LeftJoin</c> chain was seen on this select whose subsequent
/// <c>Where</c>/<c>OrderBy</c> may resolve member access against the outermost root scope only (any chain
/// depth), or whose subsequent <c>Select</c> may resolve a whole-entity leaf against ANY level (root or any
/// join's Inner side) — see <c>NativeTranslation.NativeJoinScopeTranslator.TryTranslateRootScopeOnly</c> and
/// <c>NativeTranslation.NativeJoinScopeProjectionBinder</c> respectively. Recording this chain is pure
/// metadata, built EAGERLY at join-registration time for every join (mirroring exactly how a single join's
/// scope is built eagerly today) — see
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore</c>'s per-join eligibility check.
/// Confirming the join (registering its <c>$lookup</c>, flipping <c>Route</c> away from <c>Fallback</c>)
/// stays a SEPARATE, later, deferred step at the consuming <c>Select</c> arm, unaffected by how eagerly this
/// metadata itself is built. See <c>docs/superpowers/specs/2026-09-07-native-chained-join-scope-design.md</c>.
/// </summary>
internal sealed class MongoJoinScope(
    Microsoft.EntityFrameworkCore.Metadata.IEntityType outerEntityType, IReadOnlyList<MongoJoinScopeLevel> levels)
{
    public Microsoft.EntityFrameworkCore.Metadata.IEntityType OuterEntityType { get; } = outerEntityType;

    /// <summary>One entry per join, root-most first. <c>Levels.Count == 1</c> is today's single-join shape.</summary>
    public IReadOnlyList<MongoJoinScopeLevel> Levels { get; } = levels;
}

/// <summary>One join level in a (possibly chained) native join scope.</summary>
internal sealed class MongoJoinScopeLevel(
    Microsoft.EntityFrameworkCore.Metadata.IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)
{
    public Microsoft.EntityFrameworkCore.Metadata.IEntityType InnerEntityType { get; } = innerEntityType;

    /// <summary>The <c>$lookup</c> alias (<c>joinInfo.Alias</c>) this level's inner-scope field refs are prefixed with.</summary>
    public string InnerPrefix { get; } = innerPrefix;

    /// <summary>Whether this level's join is left-outer (<c>LeftJoin</c>) or inner (<c>Join</c>).</summary>
    public bool IsLeftOuter { get; } = isLeftOuter;
}
```

(Use the proper `using Microsoft.EntityFrameworkCore.Metadata;` instead of fully-qualified names in the real
edit — spelled out inline above only so this block is copy-pasteable without missing a using.)

- [ ] **Step 2: Fix the two depth-1 call sites that construct/read the old flat shape**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs` around line 2220 (today's single-join construction
site inside the `if (joinInfo.Navigation is { } eligibleNavigation ...)` block guarded by
`outerQueryExpression.Select.JoinScope == null`), change:

```csharp
outerQueryExpression.Select.JoinScope = new MongoJoinScope(
    outerQueryExpression.CollectionExpression.EntityType, eligibleNavigation.TargetEntityType,
    joinInfo.Alias, joinInfo.IsLeftOuter);
```

to:

```csharp
outerQueryExpression.Select.JoinScope = new MongoJoinScope(
    outerQueryExpression.CollectionExpression.EntityType,
    [new MongoJoinScopeLevel(eligibleNavigation.TargetEntityType, joinInfo.Alias, joinInfo.IsLeftOuter)]);
```

In `NativeJoinScopeProjectionBinder.cs` and `IsSingleEligibleNativeJoinScope`'s callers, replace every read
of `scope.InnerEntityType`/`scope.InnerPrefix`/`scope.IsLeftOuter` with `scope.Levels[0].InnerEntityType`/
`.InnerPrefix`/`.IsLeftOuter` (search for these three property names across
`NativeTranslation/NativeJoinScopeProjectionBinder.cs` and `NativeTranslation/NativeJoinScopeTranslator.cs`'s
existing depth-1 entry points — `TryTranslateCore`'s `scope.OuterEntityType`/`scope.InnerEntityType`/
`scope.InnerPrefix` reads become `scope.OuterEntityType`/`scope.Levels[0].InnerEntityType`/
`scope.Levels[0].InnerPrefix`).

- [ ] **Step 3: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: compile errors at every remaining flat-property read — fix each by indexing `Levels[0]`, per Step 2,
until it builds clean.

- [ ] **Step 4: Run the existing join unit/functional suites to confirm zero behavior change at depth 1**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"`
Expected: PASS, identical to pre-change (this step is a pure rename — no new capability yet).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs
git commit -m "refactor: make MongoJoinScope chain-capable (Levels list, depth-1 behavior unchanged)"
```

---

### Task 3: Build `JoinScope` chain metadata eagerly for every join

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/JoinInfo.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:2213-2223`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` (new unit test, see Step 3)

**Interfaces:**
- Consumes: `JoinInfo` (`InnerEntityType`, `IsLeftOuter`, `Navigation`, `Alias`, `Lookup`) from
  `Expressions/JoinInfo.cs`; `JoinLookupImplementsKeySelectors` (existing private method in the same file,
  signature unchanged); `outerQueryExpression.Select.IsGroupBy`/`.IsDistinct`,
  `innerQueryExpression.Select.IsGroupBy`/`.IsDistinct`.
- Produces: a new `JoinInfo.IsNativelyEligible` bool, set once per join. For a chain where every join so far
  is `IsNativelyEligible`, `outerQueryExpression.Select.JoinScope` is a `MongoJoinScope` with one
  `MongoJoinScopeLevel` per join. **This task does NOT call `MarkReferenceIncludeConfirmed`/
  `MarkJoinLookupConfirmed`/`AddLookup`** — those stay exactly where they are today (the Select-side arms);
  `HasUnconfirmedCandidateJoin` is UNCHANGED by this task, deliberately (Task 6 is what makes those calls,
  generalized to a chain).

- [ ] **Step 1: Add `IsNativelyEligible` to `JoinInfo`**

```csharp
// JoinInfo.cs — new property alongside the existing Navigation/Alias/Lookup ones
/// <summary>
/// Whether THIS join, on its own, satisfies every conjunct the native join-scope mechanism requires
/// (a resolved navigation, not a left-outer collection nav, its $lookup reproduces the written key
/// equality, and neither side is GroupBy/Distinct-sourced) — set once, at join-registration time, by
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore</c>. A CHAIN is native-scope-
/// eligible only when every join in it is; see <c>MongoSelectDefinition.JoinScope</c>.
/// </summary>
public bool IsNativelyEligible { get; set; }
```

- [ ] **Step 2: Generalize the eligibility check and chain (re)build**

Replace the existing block at `MongoQueryableMethodTranslatingExpressionVisitor.cs:2213-2223`:

```csharp
if (joinInfo.Navigation is { } eligibleNavigation
    && innerQueryExpression.Select.IsBareCollectionScan
    && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
    && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
    && outerQueryExpression.Select.JoinScope == null
    && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector))
{
    outerQueryExpression.Select.JoinScope = new MongoJoinScope(
        outerQueryExpression.CollectionExpression.EntityType, eligibleNavigation.TargetEntityType,
        joinInfo.Alias, joinInfo.IsLeftOuter);
}
```

with:

```csharp
// Per-join eligibility, computed unconditionally (no longer gated on "is this the first join") —
// every join records its own verdict so a later join can find out whether EVERY join so far, itself
// included, qualifies. See docs/superpowers/specs/2026-09-07-native-chained-join-scope-design.md,
// Component 2 — this builds ONLY the JoinScope metadata; confirming the join ($lookup registration,
// Route) stays deferred to the consuming Select arm (Task 6), exactly as depth-1 already works today.
joinInfo.IsNativelyEligible =
    joinInfo.Navigation is { } eligibleNavigation
    && innerQueryExpression.Select.IsBareCollectionScan
    && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
    && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
    && !(joinInfo.IsLeftOuter && eligibleNavigation.IsCollection)
    && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);

// Rebuild the chain from scratch each time: it covers exactly the LEADING run of eligible joins, so the
// moment any join is ineligible, JoinScope stops being extended past it (a "chain with a hole" is not
// attempted — see the spec's Component 2 note on partial eligibility).
if (outerQueryExpression.Joins.All(j => j.IsNativelyEligible))
{
    outerQueryExpression.Select.JoinScope = new MongoJoinScope(
        outerQueryExpression.CollectionExpression.EntityType,
        outerQueryExpression.Joins
            .Select(j => new MongoJoinScopeLevel(j.InnerEntityType, j.Alias, j.IsLeftOuter))
            .ToList());
}
```

Note the removed `!(joinInfo.IsLeftOuter && eligibleNavigation.IsCollection)` conjunct: today this check
lives inside `IsSingleEligibleNativeJoinScope` (the CONFIRMING gate), not in this eligibility computation —
moving/duplicating it here is deliberate, because a CHAIN's later joins need to know a PRIOR join's
left-outer/collection status too (via `IsNativelyEligible`), not just the last one's. Confirm
`IsSingleEligibleNativeJoinScope`'s own copy of this check becomes redundant (both true or both false for a
single join) rather than contradictory — Task 6 removes the now-duplicate check from that method when it
widens it, rather than leaving two copies to drift apart.

- [ ] **Step 3: Unit test the metadata directly**

Add to (or create) a unit test file, following the exact harness pattern
`JoinScopeWhereSlotPopulationTests.TranslateJoinQuery` uses (three-source variant: extend that helper, or
copy it with a third `IQueryable<T>` parameter and a third `db.Set<T>()` argument, following the same
`IQueryTranslationPreprocessorFactory` → `IQueryableMethodTranslatingExpressionVisitorFactory` pipeline):

```csharp
[Fact]
public void Two_eligible_chained_joins_build_a_two_level_scope_metadata_only()
{
    var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
        owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == "Alice"));

    Assert.NotNull(mongoQ.Select.JoinScope);
    Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);

    // Confirmation is a SEPARATE, later step (Task 6) — this task deliberately does not flip Route or
    // HasUnconfirmedCandidateJoin, so both still read exactly as they did before this task.
    Assert.True(mongoQ.Select.HasUnconfirmedCandidateJoin);
}
```

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Two_eligible_chained_joins_build_a_two_level_scope_metadata_only"`
Expected: PASS.

- [ ] **Step 4: Confirm depth-1 and ineligible-chain behavior is unchanged**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"`
Expected: PASS, identical pass/fail set to before this task (in particular
`Chained_second_join_still_declines_cleanly_in_NativeOnly` must still pass — that test's shape is
`Join(...).Select(x => x.Outer).Join(...)`, i.e. the FIRST join's result is flattened back to a plain entity
via an intervening `Select` before the second `Join` runs, so `Joins.Count` resets to 1 from the second
join's perspective by the time it's processed — confirm this by reading the test, not by assuming it from
the name).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/JoinInfo.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/
git commit -m "feat: build JoinScope chain metadata eagerly for every eligible leading join run"
```

---

### Task 4: `NativeJoinScopeTranslator.TryTranslateRootScopeOnly`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs`

**Interfaces:**
- Consumes: `MongoTransparentScopeResolver.ScopeRerootingVisitor` (`ResolvedScope`, `CrossScope`), `MongoJoinScope.Levels`.
- Produces: `NativeJoinScopeTranslator.TryTranslateRootScopeOnly(MongoJoinScope scope, ParameterExpression
  rootParam, Expression body, bool valueMode, out MongoExpression? result)` — used by Task 5's `Where`/`OrderBy`
  arms whenever `scope.Levels.Count > 1`.

- [ ] **Step 1: Write the failing unit test**

```csharp
[Fact]
public void TryTranslateRootScopeOnly_translates_a_root_scoped_predicate_over_a_two_level_chain()
{
    var scope = new MongoJoinScope(_ownerEntityType, [
        new MongoJoinScopeLevel(_orderEntityType, "_lookup_Order", isLeftOuter: false),
        new MongoJoinScopeLevel(_lineEntityType, "_lookup_Line", isLeftOuter: false)
    ]);

    // ti => ti.Outer.Outer.Name == "Alice"   (root-scoped: Owner.Name)
    var ti = Expression.Parameter(typeof(TwoLevelTransparentIdentifier), "ti");
    var body = Expression.Equal(
        Expression.Property(Expression.Property(Expression.Property(ti, "Outer"), "Outer"), "Name"),
        Expression.Constant("Alice"));

    var success = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
        scope, ti, body, valueMode: false, out var result);

    Assert.True(success);
    Assert.NotNull(result);
}

[Fact]
public void TryTranslateRootScopeOnly_declines_a_predicate_touching_any_inner_level()
{
    var scope = new MongoJoinScope(_ownerEntityType, [
        new MongoJoinScopeLevel(_orderEntityType, "_lookup_Order", isLeftOuter: false),
        new MongoJoinScopeLevel(_lineEntityType, "_lookup_Line", isLeftOuter: false)
    ]);

    // ti => ti.Outer.Inner.Total == 5   (touches the FIRST join's Inner side — must decline)
    var ti = Expression.Parameter(typeof(TwoLevelTransparentIdentifier), "ti");
    var body = Expression.Equal(
        Expression.Property(Expression.Property(Expression.Property(ti, "Outer"), "Inner"), "Total"),
        Expression.Constant(5));

    var success = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
        scope, ti, body, valueMode: false, out _);

    Assert.False(success);
}
```

(`TwoLevelTransparentIdentifier`/`_ownerEntityType` etc. — reuse whatever fixture shape
`NativeJoinScopeTranslatorTests.cs` already declares for its existing flat-shape tests; a two-level nested
transparent-identifier type is `TransparentIdentifier<TransparentIdentifier<Owner, Order>, Line>` — construct
it the same way the existing file's `TryGetOuterOrInnerMemberType` tests already build a one-level one, just
nested once more.)

- [ ] **Step 2: Run and confirm both fail (method doesn't exist yet)**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~TryTranslateRootScopeOnly"`
Expected: build error (`TryTranslateRootScopeOnly` doesn't exist).

- [ ] **Step 3: Implement**

```csharp
// NativeJoinScopeTranslator.cs — new public entry point, alongside TryTranslatePredicate/TryTranslateValue

/// <summary>
/// Resolves member access over a CHAINED join's nested <c>TransparentIdentifier(TransparentIdentifier(...),
/// Inner)</c> shape, restricted to the OUTERMOST root scope only (scope index 0) — no Inner access at any
/// level is admitted. Used once <paramref name="scope"/>.Levels.Count > 1; the existing flat single-hop
/// entry points (<see cref="TryTranslatePredicate"/>/<see cref="TryTranslateValue"/>) remain the depth-1 path
/// and are unchanged. See docs/superpowers/specs/2026-09-07-native-chained-join-scope-design.md, Component 3.
/// </summary>
public static bool TryTranslateRootScopeOnly(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    var sourceCount = scope.Levels.Count;

    // scopeParams: index 0 is the root scope's own synthetic parameter; indices 1..sourceCount are each
    // level's Inner synthetic parameter (unused here — CrossScope/ResolvedScope!=0 rejects any access to
    // them — but ScopeRerootingVisitor's constructor still needs one entry per index).
    var rootScopeParam = Expression.Parameter(scope.OuterEntityType.ClrType, "rootScope");
    var scopeParams = new ParameterExpression[sourceCount + 1];
    scopeParams[0] = rootScopeParam;
    for (var i = 0; i < sourceCount; i++)
    {
        scopeParams[i + 1] = Expression.Parameter(scope.Levels[i].InnerEntityType.ClrType, $"innerScope{i}");
    }

    var visitor = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
        rootParam, hopNames: ["Outer", "Inner"], sourceCount, scopeParams);
    var rewritten = visitor.Visit(body);

    if (visitor.CrossScope || visitor.ResolvedScope is not 0)
        return false;

    var translator = new MongoExpressionTranslator(scope.OuterEntityType);
    return valueMode
        ? translator.TryTranslateValue(rewritten, out result)
        : translator.TryTranslate(rewritten, out result);
}
```

Check `MongoTransparentScopeResolver.ScopeRerootingVisitor`'s actual constructor accessibility
(`NativeTranslation/MongoTransparentScopeResolver.cs:87-89` — it's `internal sealed class`, nested in an
`internal static class` in the same `NativeTranslation` namespace as `NativeJoinScopeTranslator`, so it is
directly usable without a using-alias or visibility change).

- [ ] **Step 4: Run tests again**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~TryTranslateRootScopeOnly"`
Expected: PASS for both.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs
git commit -m "feat: NativeJoinScopeTranslator.TryTranslateRootScopeOnly for chained join scopes"
```

---

### Task 5: `NativeSlotPopulator` — generalize `Where`, add `OrderBy`/`ThenBy`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:133-189`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (Task 1's end-to-end test
  stays red until Task 6 also lands — see Step 3; add narrower unit-level coverage in this task instead)

**Interfaces:**
- Consumes: `NativeJoinScopeTranslator.TryTranslateRootScopeOnly` (Task 4), the existing depth-1
  `TryTranslatePredicate`/`TryTranslateValue`/`ReferencesInnerScope`.
- Produces: `Where`/`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending` now populate
  `PipelineOps`/sort for both depth-1 (unchanged) and depth ≥ 2 (new) join scopes, provided the
  predicate/key selector only touches the root scope.

- [ ] **Step 1: Generalize the `Where` arm**

Replace lines 159-163:

```csharp
else if (mongoQ.Select.JoinScope is { } joinScope
         && !NativeJoinScopeTranslator.ReferencesInnerScope(predicate.Parameters[0], predicate.Body)
         && NativeJoinScopeTranslator.TryTranslatePredicate(
             joinScope, predicate.Parameters[0], predicate.Body, out var joinPredicateNode))
    mongoQ.Select.AddPredicateConjunct(joinPredicateNode);
```

with:

```csharp
else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
         && !NativeJoinScopeTranslator.ReferencesInnerScope(predicate.Parameters[0], predicate.Body)
         && NativeJoinScopeTranslator.TryTranslatePredicate(
             singleLevelScope, predicate.Parameters[0], predicate.Body, out var joinPredicateNode))
    mongoQ.Select.AddPredicateConjunct(joinPredicateNode);
else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
         && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
             chainedScope, predicate.Parameters[0], predicate.Body, valueMode: false, out var chainedPredicateNode))
    mongoQ.Select.AddPredicateConjunct(chainedPredicateNode);
```

- [ ] **Step 2: Add the `OrderBy`/`OrderByDescending` join-scope fallback**

Replace lines 167-178:

```csharp
else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
{
    var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
    var ascending = methodDefinition == QueryableMethods.OrderBy;
    translator.SelfParam = keySelector.Parameters[0];
    if (translator.TryTranslateField(keySelector.Body, out var keyNode))
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));
    else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(computedKey, ascending));
    else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelSortScope
             && !NativeJoinScopeTranslator.ReferencesInnerScope(keySelector.Parameters[0], keySelector.Body)
             && NativeJoinScopeTranslator.TryTranslateValue(
                 singleLevelSortScope, keySelector.Parameters[0], keySelector.Body, out var joinSortKey))
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(joinSortKey, ascending));
    else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedSortScope
             && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
                 chainedSortScope, keySelector.Parameters[0], keySelector.Body, valueMode: true, out var chainedSortKey))
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(chainedSortKey, ascending));
    else
        mongoQ.Select.MarkNotNativelyRepresentable();
}
```

Apply the identical five-branch structure to the `ThenBy`/`ThenByDescending` arm just below it (lines
179-189 in the pre-change file), swapping `StartOrReplaceSort` for `AppendThenBy` — same conjuncts, same
ordering of attempts (plain field → computed key → depth-1 join scope → chained join scope → decline).

- [ ] **Step 3: Run Task 1's test — expected still red, for a narrower reason**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly"`

Expected: **still FAILS** with `NativeTranslationNotSupportedException` — this task only fixes `Where`/
`OrderBy`; the query's implicit trailing Select (EF's pending-selector-applied-last projection of `{ o, r,
l }`) still isn't recognized as a chain by `IsSingleEligibleNativeJoinScope`/`NativeJoinScopeProjectionBinder`
until Task 6 lands. Confirm the failure is still exactly this exception type (not a new one) — that's the
signal this task's own change is correct even though the end-to-end test isn't green yet. Add a narrower,
`PipelineOps`-level unit test analogous to Task 3's Step 3 to prove THIS task's own piece in isolation (a
`Where`/`OrderBy` over the chain populates `PipelineOps`/sort, exactly like
`JoinScopeWhereSlotPopulationTests.Where_reading_outer_scope_after_join_populates_predicate_natively` does
for depth 1) — that test, not the end-to-end one, is this task's real green signal.

- [ ] **Step 4: Run the full join and slot-populator suites**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin|FullyQualifiedName~NativeSlotPopulator|FullyQualifiedName~JoinScopeWhereSlotPopulation"`
Expected: PASS, zero regressions.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "feat: native Where/OrderBy/ThenBy over a chained join scope (root scope only)"
```

---

### Task 6: Widen the Select-side confirming gate and `NativeJoinScopeProjectionBinder` to a chain

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:797-816`
  (`IsSingleEligibleNativeJoinScope`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `MongoTransparentScopeResolver.TryResolveScopeDepth` (already used by Task 4);
  `MongoSelectDefinition.JoinScope`/`.Levels` (Task 2/3).
- Produces: `IsSingleEligibleNativeJoinScope` succeeds for a fully-eligible chain (not just `Joins.Count ==
  1`); `NativeJoinScopeProjectionBinder.TryBindProjection` admits a whole-entity leaf naming any scope index
  in the chain (root or any join's Inner side), confirming the whole chain (`AddLookup` per level,
  `MarkReferenceIncludeConfirmed` per level, `MarkJoinLookupConfirmed`) on success. This is what makes Task
  1's end-to-end test — and `Multiple_joins_Where_Order_Any` — actually go native.

- [ ] **Step 1: Widen `IsSingleEligibleNativeJoinScope`**

Read the full existing method body first (`MongoQueryableMethodTranslatingExpressionVisitor.cs:797-` through
its closing brace, including the paging/reducing conjuncts that follow the left-outer/collection check shown
in Task 3's remarks) — this step touches every line that reads `mongoQueryExpression.Joins[0]` specifically
by index. Replace the single-join read:

```csharp
if (mongoQueryExpression.Select.JoinScope is null
    || mongoQueryExpression.Select.HasUnsupportedOperator
    || mongoQueryExpression.Select.HasTerminalOperator
    || mongoQueryExpression.Select.UnwindSource != null
    || mongoQueryExpression.Joins.Count != 1)
{
    return false;
}

var candidate = mongoQueryExpression.Joins[0];
if (candidate.Lookup is not { } lookup
    || (candidate.IsLeftOuter && lookup.Navigation is { IsCollection: true }))
{
    return false;
}
```

with:

```csharp
if (mongoQueryExpression.Select.JoinScope is not { } scope
    || scope.Levels.Count != mongoQueryExpression.Joins.Count
    || mongoQueryExpression.Select.HasUnsupportedOperator
    || mongoQueryExpression.Select.HasTerminalOperator
    || mongoQueryExpression.Select.UnwindSource != null)
{
    return false;
}

// Task 3's IsNativelyEligible already re-checked navigation/left-outer-collection/key-selector-implements
// per join before JoinScope was (re)built to cover Joins.Count levels — scope.Levels.Count ==
// Joins.Count above is proof every join already passed those conjuncts, so this method's OWN copy of the
// left-outer/collection re-check (previously duplicated here) is removed rather than left to drift.
var candidate = mongoQueryExpression.Joins[^1];
if (candidate.Lookup is not { } lookup)
{
    return false;
}
```

(`Joins[^1]` — the LAST join — is what the caller's `joinInfo` output parameter continues to mean for the
depth-1 bare/wrapped-leaf callers below this method; verify both call sites (`TranslateSelect`'s bare-leaf
and wrapped-leaf arms) only ever use the returned `joinInfo` for its `.Lookup` in the depth-1 case, and for
a chain, `NativeJoinScopeProjectionBinder.TryBindProjection` (Step 2 below) reads `mongoQueryExpression.Select.JoinScope`
directly rather than the single `joinInfo` — if any call site instead assumes `joinInfo` describes the WHOLE
chain, that call site needs its own fix here too; read both before assuming otherwise.)

- [ ] **Step 2: Generalize `NativeJoinScopeProjectionBinder`'s per-leaf recognition**

Read the existing `TryBindProjection` in full first. Find the leaf-recognition step keyed on
`IsTransparentIdentifierOuterOrInnerAccess` (the flat Outer/Inner check) and replace it with a call to
`MongoTransparentScopeResolver.TryResolveScopeDepth(leafBody, selectorParam, hopNames: ["Outer", "Inner"],
sourceCount: joinScope.Levels.Count, out var scopeIndex)`. On success:
- `scopeIndex == 0` → the leaf is the root entity (`joinScope.OuterEntityType`), staged exactly like today's
  bare Outer leaf (no alias prefix, reads straight off the root document).
- `scopeIndex == k` (`1 <= k <= joinScope.Levels.Count`) → the leaf is `joinScope.Levels[k - 1]`'s Inner
  entity, staged under `joinScope.Levels[k - 1].InnerPrefix` exactly like today's bare Inner leaf (today's
  single `joinScope.InnerEntityType`/`.InnerPrefix` reads become `joinScope.Levels[k-1].InnerEntityType`/
  `.InnerPrefix`).

Every level actually named by at least one leaf needs its `$lookup` registered — walk
`joinScope.Levels`/`mongoQueryExpression.Joins` by matching index and call `AddLookup` for each one named
(today's single `AddLookup(joinInfo.Lookup)` call becomes a loop; `AddLookup` already dedupes by alias, so
calling it for a level with no leaf naming it is harmless — but only call it for named levels, to keep the
method's own reasoning about "which $lookups does this projection need" legible). On overall success, call
`MarkReferenceIncludeConfirmed()` once per level in `joinScope.Levels` (not once per leaf — two leaves naming
the same level, e.g. `new { a = x.Outer.Inner, b = x.Outer.Inner }`, must confirm that level exactly once;
today's depth-1 method already has this exact dedup concern documented for its "duplicated inner leaf" case
— reuse that reasoning, don't re-derive it) and `MarkJoinLookupConfirmed()`.

- [ ] **Step 3: Extend the unit test file**

Add to `NativeJoinScopeProjectionBinderTests.cs` a case mirroring the file's own existing
`Both_whole_entity_leaves_projection_goes_native_under_NativeOnly`-style coverage, but over a two-level chain
naming all three scopes (`new { cr = x.Outer.Outer, or = x.Outer.Inner, od = x.Inner }`) — assert
`TryBindProjection` returns `true` and that `mongoQueryExpression.Select.Projection` has three entries.

- [ ] **Step 4: Run Task 1's end-to-end test — now expected green**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly"`
Expected: PASS.

- [ ] **Step 5: Run the full join/projection-binder suites**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"`
Expected: PASS, zero regressions at depth 1 (in particular every existing
`NativeJoinScopeProjectionBinderTests`/`Both_whole_entity_leaves_projection_goes_native_under_NativeOnly`-style
case must still pass unchanged — `Levels.Count == 1` is a strict special case of everything Step 1/2 just
generalized, not a separately-maintained branch).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "feat: confirm a chained join via a whole-entity-leaves-only projection over any scope index"
```

---

### Task 7: Verify the terminal reducer/aggregate path needs no changes

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: nothing new — this task is verification, not implementation (see design doc, Component 5).

- [ ] **Step 1: Add coverage for `Count`/`First` on top of a confirmed chain (not just `Any`)**

```csharp
[Fact]
public void Chained_join_Where_terminal_Count_goes_native_under_NativeOnly()
{
    var seed = SeedOwnersAndOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Chained_join_Where_terminal_Count_goes_native_under_NativeOnly));

    var count = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
        .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
        .Where(x => x.o.Name == seed.Owners[0].Name)
        .Count();

    Assert.True(count > 0);
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_Where_terminal_Count_goes_native_under_NativeOnly"`

Expected: PASS with no production changes. **If this fails**, that is new information this plan's design didn't
predict — stop, read the failure via `superpowers:systematic-debugging`'s Phase 1 before patching anything;
do not add a guard here speculatively.

- [ ] **Step 3: Also verify `Take`/`Skip` after a confirmed chain still declines (the existing post-confirmation guard must still fire)**

```csharp
[Fact]
public void Take_after_a_confirmed_chained_join_declines_cleanly_under_NativeOnly()
{
    var seed = SeedOwnersAndOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Take_after_a_confirmed_chained_join_declines_cleanly_under_NativeOnly));

    Assert.Throws<NativeTranslationNotSupportedException>(() =>
        db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Take(1)
            .ToList());
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Take_after_a_confirmed_chained_join_declines_cleanly_under_NativeOnly"`

Expected: PASS. This confirms `HasConfirmedJoinLookup`'s existing post-confirmation slot-operator guard
(`NativeSlotPopulator.cs:95-131`) fires the same way for a chain as it does for a single join — it doesn't
inspect `Levels.Count` today, and this task's changes give it no reason to.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: verify terminal Count/Any and the post-confirmation Take guard over a chained join scope"
```

---

### Task 8: Fix and rebaseline `Multiple_joins_Where_Order_Any`

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs:1594-1601`

**Interfaces:**
- Consumes: everything from Tasks 2-6.
- Produces: the target test passing under default `Native` mode with an updated `AssertMql(...)` baseline,
  and passing under `MONGODB_EF_NATIVE_ONLY=1` (proving it actually goes native, not just an unchanged
  fallback).

- [ ] **Step 1: Run the target spec test under `NativeOnly` first, standalone**

Run:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any"
```

Expected: PASS (both `async: true/false`). This is the one true confirmation that this ticket's stated goal
is met — do not treat baseline rewriting (next step) as sufficient on its own, since (per `Query/AGENTS.md`'s
own pitfall) the default-mode MQL shape doesn't distinguish native from fallback.

- [ ] **Step 2: Rebaseline under default `Native` mode**

Run:
```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any"
```

Then `git diff` the rewritten `AssertMql(...)` in `NorthwindMiscellaneousQueryMongoTest.cs` — expect `$match`
(`City == "London"`) and `$sort` (`_id`) to now appear BEFORE the two `$lookup`/`$unwind` pairs (see design
doc's Data Flow section for why this order is both correct and expected to differ from today's fallback
baseline), followed by `$limit: 1` and the `Any` shaping. If the diff shows anything else (e.g. `$match`
still trailing the lookups, or an extra unexpected stage), that means Route did not actually flip to native
for this query — stop and re-check Task 3-6's wiring before accepting the rewrite.

- [ ] **Step 3: Rebuild and re-run without the rewrite var to confirm it's genuinely green**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any"
```
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs
git commit -m "test: rebaseline Multiple_joins_Where_Order_Any's native MQL"
```

---

### Task 9: Full multi-EF regression

**Files:** none (verification only).

- [ ] **Step 1: Run the full spec suite under `MONGODB_EF_NATIVE_ONLY=1` for EF10, diff the pass/fail counts**

Run:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build
```
Expected: at least the one target test flips `Failed -> Passed`; zero `Passed -> Failed` anywhere else. If
anything else flips to `Failed`, that's a regression from the chain-confirmation eligibility walk (Task 3) or
the generalized `Where`/`OrderBy` arms (Task 5) or the widened Select-side gate/projection binder (Task 6) —
bisect by reverting Task 6 first, then Task 5, then Task 3 (in that order: latest, most speculative changes
first).

- [ ] **Step 2: Run the full functional + unit test suites for EF10**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`
Expected: PASS, zero regressions outside the tests this plan added/rebaselined.

- [ ] **Step 3: Invoke `/test-all` for full EF8/EF9/EF10 coverage**

Nothing in this plan touches an EF-version-conditional code path, so no `#if` divergence is expected — but
confirm this rather than assume it, since a stale assumption here is exactly the kind of thing
`Query/AGENTS.md`'s multi-version pitfalls section warns about.

- [ ] **Step 4: Update `Query/AGENTS.md`'s capability summary**

Add a sentence to the `Genuine two-sided Join/LeftJoin` bullet noting that a chain of `Joins.Count >= 2` now
goes native for `Where`/`OrderBy`/`ThenBy`/terminal-reducer-or-aggregate shapes restricted to the outermost
root scope (no trailing `Select`, no Inner-side access at any level) — cross-reference this plan's spec doc.
Do not overwrite the existing depth-1 documentation; append.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "docs: record native chained-join Where/OrderBy/terminal support in Query/AGENTS.md"
```

---

## Explicitly out of scope (do not implement in this plan)

- A trailing `Select` over a chain mixing a whole-entity leaf with a **scalar or computed** leaf, or a
  **bare scalar** leaf trailing a chain — both already decline at depth 1 too (EF-444's own carve-out); Task
  6 only widens the existing whole-entity-leaves-only shape across a chain, it does not admit a new leaf kind.
- The **bare** whole-entity `Select` arm (`x => x.Outer.Outer`, selecting a single sub-scope as the entire
  query result with no wrapping `new{}`) over a chain — only the *wrapped* multi-leaf arm
  (`NativeJoinScopeProjectionBinder`) is widened in Task 6, since that's what `Multiple_joins_Where_Order_Any`
  needs. Left for a follow-up if a query shape needs it.
- Widening per-join eligibility (query-filtered targets, composite non-PK keys, computed key selectors) —
  unchanged from the existing single-join mechanism.
