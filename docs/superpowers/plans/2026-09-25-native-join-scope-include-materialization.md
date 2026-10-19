# Native join-scope whole-entity leaf: Include materialization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an Include-wrapped whole-entity leaf inside a native join-scope projection (`new { o, r.Total }`
where `o`/`r` also carries `.Include(...)`) actually materialize correct data under `MongoQueryMode.NativeOnly`,
not just stage the right `$project` shape (root cause A, already fixed and committed).

**Architecture:** `MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember` gets a new unwrap/rewrap
step so an `IncludeExpression`-wrapped join-scope leaf is rebuilt exactly like today's bare whole-entity leaf,
then re-wrapped in the same `IncludeExpression` chain. That re-wrapped node is routed through a new, narrow
entry point on `MongoProjectionBindingExpressionVisitor` — `VisitIncludeExpression` — which reuses the
EXISTING, already-correct `IncludeExpression` handling in that visitor's `VisitExtension` (collection-nav
`$lookup` registration via `RewriteCollectionIncludeForLookup`) without triggering `Translate`'s destructive
`ReplaceProjectionMapping` side effect. One additional risk, flagged but not pre-solved (verify empirically,
Task 1 Step 6): the Outer-leaf test's `Join` key happens to resolve to the SAME navigation the `Include`
targets (`Owner.Orders`), which may alias-collide with the join's own already-registered `$lookup` the same
way `TryGetCollectionIncludeOverJoinScope` (a different, unrelated arm) already documents and works around —
if it does, Task 1 grows a second step to handle it; if it doesn't, skip that step.

**Tech Stack:** C#, EF Core 8/9/10 (build configurations, not TFMs), xUnit, MongoDB aggregation pipelines.

**Spec:** `docs/superpowers/specs/2026-09-25-native-join-scope-include-materialization-design.md`

## Global Constraints

- Preserve file BOMs on every file touched.
- `src/` is nullable-enabled — no new warnings. (`MongoProjectionBindingExpressionVisitor.cs` itself has
  `#nullable disable` at the top — match that file's existing convention, don't add `#nullable enable` to it.)
- No `#if EF8`/`EF9`/`EF10` guards needed: nothing in this fix is EF-version-conditional.
- Unit tests use plain xUnit `Assert.*` — no FluentAssertions.
- The two acceptance functional tests already exist, uncommitted, in
  `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (added investigating this bug,
  currently RED): `Whole_outer_entity_leaf_that_is_also_reference_included_goes_native` and
  `Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join`. Do not rewrite
  them — they encode the design doc's two spikes exactly. Confirm they still read as written before Task 1.
- Docker (or `MONGODB_URI`/`ATLAS_URI`) must be available for every functional/spec test run in this plan —
  confirm with `docker info` before starting if unsure.
- `EF_TEST_REWRITE_BASELINES=1` runs (Task 4) are scoped to exactly ONE `FullyQualifiedName` at a time — never
  bulk.

## Review Focus

- **The alias-collision hypothesis in Task 1 must be verified, not assumed either way.** A reviewer should
  check that Task 1 Step 6 actually ran the test and read real output before deciding whether the extra
  mitigation step was needed — a plan that "looks done" without that evidence is not done.
- **The re-wrap must preserve the ORIGINAL, untouched `NavigationExpression`** (a `ThenInclude` chain, or the
  plain member access `RewriteCollectionIncludeForLookup`/`ExtractNestedIncludePipeline` expect to walk) — only
  `EntityExpression` changes across the unwrap/rebuild/rewrap. Pinned by Task 1's own test (no `ThenInclude` in
  either acceptance test, but the code must not assume there never is one elsewhere in the codebase).
- **`VisitIncludeExpression` must not leak `_queryExpression` state** — it must restore whatever value was
  there before (`null`, in every known caller today, but written to survive a future caller that isn't).
  Pinned by Task 1 Step 2's implementation itself (`try`/`finally`).
- **A non-Include whole-entity leaf in the SAME projection as an Include-wrapped one must be unaffected** —
  `BindResultMember`'s unwrap loop is a no-op (`includeWrappers.Count == 0`) for a leaf that was never
  `IncludeExpression`-wrapped, so it must return exactly what it returns today. Pinned implicitly by the full
  regression sweep in Task 5 (every existing `NativeJoinTests.cs`/`NativeJoinScopeProjectionBinderTests.cs`
  test that mixes a bare whole-entity leaf with something else must still pass).
- **A query in `MongoQueryMode.DriverLinq`** (not `NativeOnly`/`Native`) must be completely unaffected by this
  fix — `BindResultMember` is only reached once a query has already been routed onto the native
  wrapped-leaf-join arm, which `DriverLinq` never takes. Both acceptance tests assert the `DriverLinq` leg too
  (see their existing bodies) — Task 1/2 must not accidentally break that leg while fixing the native one.

---

### Task 1: Wire the Include bridge and get the Outer-leaf test green

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test (already exists, uncommitted): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Produces: `MongoProjectionBindingExpressionVisitor.VisitIncludeExpression(MongoQueryExpression, IncludeExpression) : Expression` — Task 2/3 don't call this directly (they exercise it transitively through the same `BindResultMember` fix), but any FUTURE arm needing the same bridge should reuse this method rather than re-deriving it.
- Consumes: `MongoProjectionBindingExpressionVisitor`'s existing private `_queryExpression` field and `VisitExtension`'s existing `case IncludeExpression` (line ~507 today) and `case StructuralTypeShaperExpression structuralTypeShaperExpression` (line ~461 today, the "already bound by index" arm) — unchanged by this task, just reached from a new caller.

- [ ] **Step 1: Confirm the two existing functional tests are present and red**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Whole_outer_entity_leaf_that_is_also_reference_included_goes_native|FullyQualifiedName~Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join"
```
Expected: both FAIL. The Outer test fails with `System.FormatException: Element '_lookup_Orders' does not
match any field or property of class Owner`. The Inner test fails with an `Assert.Equal` collection mismatch
(`Alice`'s `Total` reads as `null` instead of `10`). If either fails differently (e.g. compiles differently,
throws something else), STOP and use `superpowers:systematic-debugging` — the starting state this plan
assumes has changed since the design doc was written.

- [ ] **Step 2: Add the `VisitIncludeExpression` bridge method**

In `MongoProjectionBindingExpressionVisitor.cs`, immediately after the closing brace of `Translate` (the method
ending `return MatchTypes(result, expression.Type); }`, right before the `/// <inheritdoc />` comment on
`Visit`), add:

```csharp
    /// <summary>
    /// Visits a single, already correctly re-bound <see cref="IncludeExpression"/> subtree — produced by
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember</c> re-wrapping a rebound
    /// join-scope whole-entity shaper — WITHOUT going through <see cref="Translate"/>'s wholesale
    /// <see cref="MongoQueryExpression.ReplaceProjectionMapping"/> side effect. The caller's own projection
    /// mapping (already correctly built via index-based <see cref="MongoQueryExpression.AddToProjection"/>
    /// calls) must survive untouched; only <see cref="VisitExtension"/>'s existing <see cref="IncludeExpression"/>
    /// handling — registering the Include's own collection <c>$lookup</c> and rewriting the node into a
    /// reducible shape via <c>RewriteCollectionIncludeForLookup</c> — is needed here. See
    /// docs/superpowers/specs/2026-09-25-native-join-scope-include-materialization-design.md, "Design options",
    /// Option B (confirmed correct by reading <c>RewriteCollectionIncludeForLookup</c>'s own
    /// "already wrapped in one or more IncludeExpressions" handling, which anticipates exactly this shape).
    /// </summary>
    internal Expression VisitIncludeExpression(MongoQueryExpression queryExpression, IncludeExpression includeExpression)
    {
        var previousQueryExpression = _queryExpression;
        _queryExpression = queryExpression;
        try
        {
            return Visit(includeExpression);
        }
        finally
        {
            _queryExpression = previousQueryExpression;
        }
    }
```

- [ ] **Step 3: Thread the visitor instance through `BuildSelectManyWrappedShaper` and `BuildSelectManyResultShaper`**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`:

Find `private static ShapedQueryExpression BuildSelectManyWrappedShaper(` (currently line 3302) and change its
signature and its one internal call to `BuildSelectManyResultShaper`:

```csharp
    private static ShapedQueryExpression BuildSelectManyWrappedShaper(
        ShapedQueryExpression source, MongoQueryExpression mongoQueryExpression, LambdaExpression collectionSelector,
        LambdaExpression resultSelector, MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor)
```

and inside its body, change:
```csharp
        var innerShaper = BuildSelectManyResultShaper(mongoQueryExpression, innerLambda.Body);
```
to:
```csharp
        var innerShaper = BuildSelectManyResultShaper(mongoQueryExpression, innerLambda.Body, projectionBindingExpressionVisitor);
```

Update its one call site (currently line 3258):
```csharp
        return BuildSelectManyWrappedShaper(source, mongoQueryExpression, collectionSelector, resultSelector);
```
to:
```csharp
        return BuildSelectManyWrappedShaper(source, mongoQueryExpression, collectionSelector, resultSelector, _projectionBindingExpressionVisitor);
```

Now find `private static Expression BuildSelectManyResultShaper(` (currently line 3325) and change its
signature (the new parameter goes before the existing optional `foldedBody = null`, since optional parameters
must stay last):

```csharp
    private static Expression BuildSelectManyResultShaper(
        MongoQueryExpression mongoQueryExpression, Expression projectionBody,
        MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor, Expression? foldedBody = null)
```

Inside its body, change the `BindResultMember` call:
```csharp
            boundValues[i] = BindResultMember(
                mongoQueryExpression, members[i].MemberName, members[i].Value,
                foldedMembers is not null && i < foldedMembers.Count ? foldedMembers[i].Value : null);
```
to:
```csharp
            boundValues[i] = BindResultMember(
                mongoQueryExpression, members[i].MemberName, members[i].Value, projectionBindingExpressionVisitor,
                foldedMembers is not null && i < foldedMembers.Count ? foldedMembers[i].Value : null);
```

Update its other two call sites. First, in the bare/wrapped `SelectMany` branch (currently around line 413):
```csharp
                : BuildSelectManyResultShaper(mongoQueryExpression, selector.Body);
```
to:
```csharp
                : BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, _projectionBindingExpressionVisitor);
```

Second, in `TranslateSelect`'s wrapped-leaf-join arm (currently around line 618):
```csharp
            return source.UpdateShaperExpression(
                BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, foldedJoinBody));
```
to:
```csharp
            return source.UpdateShaperExpression(
                BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, _projectionBindingExpressionVisitor, foldedJoinBody));
```

- [ ] **Step 4: Rewrite `BindResultMember` to unwrap, rebuild, re-wrap, and bridge**

Find `private static Expression BindResultMember(` (currently line 3377). Replace its full body:

```csharp
    private static Expression BindResultMember(
        MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression,
        MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor, Expression? foldedExpression)
    {
        // EF-322 Phase 2 Group B (root cause A2 — materialization half): a whole-entity leaf that is ALSO the
        // target of an Include/ThenInclude arrives here as IncludeExpression { EntityExpression: <the folded
        // shaper>, ... } rather than the bare StructuralTypeShaperExpression itself. Unwrap down to the
        // innermost EntityExpression before the shape check below (mirroring
        // NativeJoinScopeProjectionBinder.TryBindProjection's own unwrap for the SAME reason), rebuild the
        // shaper exactly as before, then re-wrap the REBUILT shaper back inside the SAME chain of
        // IncludeExpressions (innermost first) — preserving each wrapper's own, untouched NavigationExpression
        // — and route the re-wrapped chain through VisitIncludeExpression so the Include's own $lookup
        // registration and node-rewrite (MongoProjectionBindingExpressionVisitor.VisitExtension's
        // IncludeExpression case) actually runs. Without this, the shape check below fails on the wrapped
        // leaf, execution falls through to BindSelectManyMember, and the RAW (Include-wrapped, unfolded) leaf
        // gets registered under the member's own alias — a field the native $project never emits under (the
        // Outer leaf emits under $$ROOT, the Inner leaf under its own fixed InnerPrefix, never the member's
        // alias) — silently misreading the whole leaf. See the design doc's "Root cause, precisely" section
        // for the full trace (docs/superpowers/specs/2026-09-25-native-join-scope-include-materialization-design.md).
        var includeWrappers = new List<IncludeExpression>();
        var unwrappedFoldedExpression = foldedExpression;
        while (unwrappedFoldedExpression is IncludeExpression includeToUnwrap)
        {
            includeWrappers.Add(includeToUnwrap);
            unwrappedFoldedExpression = includeToUnwrap.EntityExpression;
        }

        if (unwrappedFoldedExpression is StructuralTypeShaperExpression shaper
            && shaper.ValueBufferExpression is ProjectionBindingExpression shaperBinding)
        {
            var entityProjection = shaperBinding.Index is int existingIndex
                                   && shaperBinding.QueryExpression == mongoQueryExpression
                ? (EntityProjectionExpression)mongoQueryExpression.Projection[existingIndex].Expression
                : (EntityProjectionExpression)mongoQueryExpression.GetMappedProjection(shaperBinding.ProjectionMember!);

            var entityIndex = mongoQueryExpression.AddToProjection(entityProjection, alias);

            Expression rebound = shaper.Update(
                new ProjectionBindingExpression(mongoQueryExpression, entityIndex, typeof(ValueBuffer)));

            if (includeWrappers.Count == 0)
            {
                return rebound;
            }

            for (var i = includeWrappers.Count - 1; i >= 0; i--)
            {
                rebound = includeWrappers[i].Update(rebound, includeWrappers[i].NavigationExpression);
            }

            return projectionBindingExpressionVisitor.VisitIncludeExpression(mongoQueryExpression, (IncludeExpression)rebound);
        }

        // A NESTED wrapped leaf sourced from a join scope (native-join-scope-nested-projection ticket):
        // NativeJoinScopeProjectionBinder.TryBindProjection already translated this alias into a
        // MongoDocumentConstructionExpression and staged it into mongoQueryExpression.Select.Projection (the
        // native IR list — separate from mongoQueryExpression.Projection, the EF-facing list this method builds
        // the shaper against). Falling through to BindSelectManyMember below would register the RAW, untranslated
        // valueExpression (the original `new { Name = o.Customer!.Name }` NewExpression) under this alias
        // instead — MongoProjectionBindingRemovingExpressionVisitor's MongoDocumentConstructionExpression case
        // (VisitExtension) would then never match, and the shaper would fall through to an ordinary alias read
        // that hands the WHOLE anonymous member type to BsonBinding.GetElementValue<T>, which has no serializer
        // for an anonymous type and throws (MEASURED: "Unsupported collection type '<>f__AnonymousTypeN<...>'").
        // Registering the ALREADY-TRANSLATED MongoDocumentConstructionExpression instead (not the raw
        // valueExpression) is what makes the downstream MongoDocumentConstructionExpression case fire and read
        // each member back via its own dotted alias.memberName path.
        //
        // The lookup goes through MongoSelectDefinition.TryGetDocumentConstructionProjection — the SAME method
        // MongoProjectionBindingExpressionVisitor.TryGetNativeDocumentConstructionLeaf uses for the plain-root
        // EF-447 leaf — rather than a local alias scan. Final-review Finding 3: the two used to be near-
        // identical scans with different admission rules (this one omitted the Route == Projection check, the
        // CLR-type check and the alias-override mapping), which is exactly how a looser lookup ends up matching
        // a staged node the stricter one refused and reading it back under a member it does not describe.
        if (mongoQueryExpression.Select.TryGetDocumentConstructionProjection(
                alias, valueExpression.Type, out var construction))
        {
            var constructionIndex = mongoQueryExpression.AddToProjection(construction, alias);
            return new ProjectionBindingExpression(mongoQueryExpression, constructionIndex, valueExpression.Type);
        }

        return BindSelectManyMember(mongoQueryExpression, alias, valueExpression);
    }
```

Only the top comment block and the new unwrap/rewrap/bridge logic (through the `return
projectionBindingExpressionVisitor.VisitIncludeExpression(...)` line) are new. The
`TryGetDocumentConstructionProjection` fallback block and the final `BindSelectManyMember` call are byte-for-byte
unchanged from before — do not alter them.

- [ ] **Step 5: Build and run the Outer-leaf test**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Whole_outer_entity_leaf_that_is_also_reference_included_goes_native"
```

Three possible outcomes:

**(a) PASS.** The bridge alone was sufficient — the alias-collision hypothesis in the design doc did not
manifest (most likely because `AddLookup`'s alias-based dedup, or `RewriteCollectionIncludeForLookup`'s own
handling, tolerated the coincidence harmlessly). Skip Step 6, go to Step 7.

**(b) FAILS with a data mismatch specifically on `OwnerOrderCount`** (the expected tuple's third element) —
e.g. it comes back `0` instead of the real count, or some other wrong-but-plausible number, while `Name`/`Total`
are correct. This is the alias-collision hypothesis manifesting: the join's own `$lookup` (registered under
alias `_lookup_Orders`, `ForceUnwind: true`, one document per row) and the Include's own attempted `$lookup`
registration (same alias, since `Owner.Orders` is what the join's key resolved to) collided in
`MongoQueryExpression.AddLookup` (`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.Lookup.cs`,
`AddLookup`, dedups by `l.As`) — the join's single-document lookup won, and the Include's collection shaper is
now reading a single document as if it were an array. Go to Step 6.

**(c) FAILS any other way** (an exception, a different assertion failure, a compile error). STOP — this is new
information the design doc did not anticipate. Use `superpowers:systematic-debugging` before writing any more
code; do not guess at Step 6's fix for a different failure than the one it's written for.

- [ ] **Step 6 (ONLY if Step 5 produced outcome (b)): confirm and fix the alias collision**

First confirm the hypothesis directly — add a temporary diagnostic to the test (or a throwaway `[Fact]` right
next to it) that runs the query and logs the MQL via a `CreateContext(seed, MongoQueryMode.Native, name, out
var spyLogger)` overload (see any existing test in the same file using `out var spyLogger)` and
`spyLogger.GetLogMessagesByEventId(MongoEventId.ExecutedMqlQuery)` for the pattern), and check whether the
pipeline has only ONE `$lookup` targeting the Orders collection (confirms the collision) or two (rules it out —
if it's two, this is a different bug; STOP and use `superpowers:systematic-debugging` instead of applying the
fix below). Delete the diagnostic once you've confirmed either way — it must not survive to the commit.

If confirmed, the fix is in `MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`'s wrapped-leaf-join
arm (the `else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var wrappedLeafJoin) &&
NativeJoinScopeProjectionBinder.TryBindProjection(...))` branch, currently starting at line 582): mirror the
EXISTING rename trick in the sibling `TryGetCollectionIncludeOverJoinScope` arm above it (currently lines
498–516: `if (collectionJoin.Lookup!.As == LookupExpression.GetLookupAlias(includeNavigation)) { var renamedAlias
= $"{collectionJoin.Lookup.As}_join"; collectionJoin.Lookup.As = renamedAlias; }`), but — unlike that arm, which
never reads the join's Inner side — this arm's own leaf (`r.Total` in the acceptance test) DOES read the Inner
side via `wrappedLeafJoin`'s registered `Levels[k].InnerPrefix`, which `NativeJoinScopeProjectionBinder
.TryBindProjection` already staged BEFORE this point using the ORIGINAL (pre-rename) alias. Renaming
`wrappedLeafJoin.Lookup.As` here without also updating `mongoQueryExpression.Select.JoinScope!.Levels[...]
.InnerPrefix` to the SAME new value would silently desynchronize the two — the emitted `$lookup`'s `as` would
say one thing, the already-staged `MongoElementRefExpression` reading `r.Total` would say another, and the
Inner leaf would break instead of the Outer one. There is no existing test pinning this specific interaction
(Include on the Outer entity's own collection nav, where that SAME nav is ALSO the join's key, in a projection
that ALSO reads the join's Inner side) — write one as part of this step, in
`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs`,
mirroring `Binds_a_whole_outer_entity_leaf_that_is_also_include_wrapped`'s shape but asserting on
`mongoQ.Select.JoinScope!.Levels[0].InnerPrefix` equalling whatever the renamed alias ends up being, and on the
Inner leaf's own `MongoElementRefExpression.Path` matching it — before touching production code, per
`superpowers:test-driven-development`.

- [ ] **Step 7: Run the whole `NativeJoinTests.cs` file to confirm no regressions**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"
```

Expected: PASS for every test in the file except
`Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join` (Task 2's job, not
this task's) — confirm that ONE test's failure looks the same as it did before this task started (still the
`Alice`/`null`-`Total` mismatch, not something new).

- [ ] **Step 8: Run the whole unit test project to confirm no regressions there either**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build
```

Expected: PASS, same total as before this task (plus one more if Step 6 added a test).

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
  src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
# If Step 6 ran, also:
git add tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs
git commit -m "EF-322: materialize an Include-wrapped whole-entity join-scope leaf correctly"
```

---

### Task 2: Verify (and fix if needed) the Inner-leaf / LeftJoin case

**Files:**
- Test (already exists, uncommitted from Task 1's commit): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`
- Modify (only if Step 2 below finds a gap): the same two `src/` files as Task 1.

**Interfaces:**
- Consumes: Task 1's fix, unconditionally over Outer/Inner (the unwrap/rewrap/bridge logic in `BindResultMember`
  does not branch on which scope index the leaf resolved to).

- [ ] **Step 1: Run the Inner-leaf test in isolation**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join"
```

Expected: PASS. This test's Include target (`Order.OrderLines`) is UNRELATED to the join's own key
(`Owner.Id`/`Order.OwnerId`), so the alias-collision hazard Task 1 Step 6 addressed does not apply here — Task
1's fix alone should be sufficient. If it FAILS, read the failure: a data mismatch on `LineCount` specifically
(not `Total`) would suggest a DIFFERENT collision (verify with the same MQL-dump technique as Task 1 Step 6
before assuming it's the same fix); anything else (an exception, a `Total` mismatch) means Task 1's fix has a
gap for the LeftJoin/Inner-leaf combination specifically — use `superpowers:systematic-debugging`, don't
extend Task 1 Step 6's fix blindly onto a different failure.

- [ ] **Step 2: Run the whole `NativeJoinTests.cs` file**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"
```

Expected: PASS, every test in the file, 0 failures.

- [ ] **Step 3: Commit (only if Step 1 required a code change; otherwise this task has nothing new to commit)**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
  src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs
git commit -m "EF-322: fix Include materialization for the LeftJoin Inner-leaf case"
```

If Step 1 passed with no code changes, skip this step — there is nothing to commit, and the two functional
tests were already committed in Task 1.

---

### Task 3: Prove the fix generalizes to a chain (depth > 1)

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `SeedOwnersOrdersAndLines()`, `TranslateThreeSourceJoinQuery`-equivalent functional harness (this
  file already has multi-level chain tests, e.g.
  `Chain_scalar_leaf_beside_a_whole_entity_leaf_at_a_non_adjacent_chain_level_reads_correctly` — mirror its
  `CreateContext`/seed usage, not its assertions).

- [ ] **Step 1: Write the new test**

Add to `NativeJoinTests.cs`, immediately after
`Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join`:

```csharp
[Fact]
public void Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain()
{
    // Root cause A's own Task 2 re-rooted its chain-depth unit proof onto the ROOT leaf specifically
    // (owners.Include(o => o.Orders)) rather than a non-root chain level, after measuring that Including a
    // non-root level's own reference nav can coincidentally widen the recognized join-scope chain by one
    // level (see that plan's ledger). Mirror the same choice here at the FUNCTIONAL level: a genuine
    // Levels.Count == 2 chain, with the Include on the chain's ROOT (Owner.Orders) rather than either
    // joined-in level, proving Task 1/2's BindResultMember fix generalizes to scope depth > 1 without
    // reproducing that unrelated, pre-existing chain-detection hazard.
    var seed = SeedOwnersOrdersAndLines();

    static List<(string OwnerName, int OwnerOrderCount, string Sku)> Run(JoinTestDbContext db) =>
        db.Owners.Include(o => o.Orders)
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
            .AsEnumerable()
            .OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
            .Select(x => (x.o.Name, x.o.Orders.Count, x.LineSku))
            .ToList();

    var expected = seed.Owners
        .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
        .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
        .OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
        .Select(x => (x.o.Name, seed.Orders.Count(order => order.OwnerId == x.o.Id), x.LineSku))
        .ToList();

    using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain) + "_nativeOnly");
    Assert.Equal(expected, Run(nativeOnly));

    using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
        nameof(Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain) + "_driverLinq");
    Assert.Equal(expected, Run(driverLinq));
}
```

- [ ] **Step 2: Build and run the test**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain"
```

This test's Include target (`Owner.Orders`) is the SAME navigation the first `Join`'s key resolves to — the
identical alias-collision shape Task 1 Step 6 either did or didn't need to handle. If Task 1 Step 6 ran and
added the rename mitigation, it lives in `TranslateSelect`'s wrapped-leaf-join arm, which has no
`scope.Levels.Count`-specific branch either — expect it to apply here unchanged. If Task 1 Step 6 did NOT run
(outcome (a), the bridge alone was sufficient), expect the same here too. Either way:

Expected: PASS. `BindResultMember`'s unwrap/rewrap/bridge logic does not branch on `scope.Levels.Count`, so it
is depth-agnostic by construction, exactly like root cause A's own unwrap. If it FAILS, that is new information
(a depth-specific gap the design doc's analysis did not anticipate, OR the alias-collision mitigation turning
out to be depth-1-specific in a way Step 6 didn't foresee) — use `superpowers:systematic-debugging` rather than
patching this test to match wrong behavior.

- [ ] **Step 3: Run the whole file once more**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"
```

Expected: PASS, all tests, 0 failures.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-322: prove Include materialization fix generalizes to a chained join scope"
```

---

### Task 4: Unblock the original plan's Task 4 — regenerate the two Northwind spec baselines

**Files:** none created — regenerates existing `AssertMql` baselines and runs verification only.
- Modify (via `EF_TEST_REWRITE_BASELINES=1`, not by hand):
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs`,
  `NorthwindStringIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`,
  `NorthwindEFPropertyIncludeQueryMongoTest.cs`

**Interfaces:**
- Consumes: Tasks 1–3, committed.

- [ ] **Step 1: Confirm both target tests now pass under NativeOnly, for EF10**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_reference_when_entity_in_projection|FullyQualifiedName~NorthwindIncludeQueryMongoTest.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join" \
  --logger "console;verbosity=normal"
```

Expected: both FAIL — NOT with `NativeTranslationNotSupportedException`, and NOT with a `FormatException` or a
wrong-data assertion failure from EF Core's own base test body (that would mean Tasks 1–3 didn't actually fix
the general case, only the two specific functional-test shapes — STOP and use
`superpowers:systematic-debugging`). The only acceptable failure here is an `AssertMql` mismatch: the query now
runs correctly and returns correct data, it just emits DIFFERENT MQL than the stale, pre-fix baseline recorded.

- [ ] **Step 2: Regenerate `Include_reference_when_entity_in_projection`'s baseline, one class and one test at a time**

```bash
for cls in NorthwindIncludeQueryMongoTest NorthwindStringIncludeQueryMongoTest NorthwindIncludeNoTrackingQueryMongoTest NorthwindEFPropertyIncludeQueryMongoTest; do
  EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~${cls}.Include_reference_when_entity_in_projection"
done
```

Expected: each run reports "Failed" (the rewrite signal — see
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`). Do NOT proceed without checking each file.

- [ ] **Step 3: Check for corruption, then diff**

```bash
git diff --stat tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
for f in NorthwindIncludeQueryMongoTest NorthwindStringIncludeQueryMongoTest NorthwindIncludeNoTrackingQueryMongoTest NorthwindEFPropertyIncludeQueryMongoTest; do
  grep -n '^""")[^;]' "tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/${f}.cs"
done
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs
```

Expected: the corruption-signature `grep` prints NOTHING for any file (exit code 1). The diff shows exactly ONE
changed `AssertMql(...)` block per file, all four with the SAME new MQL shape. If corruption IS found, stop —
restore from git before re-running the rewrite more narrowly.

- [ ] **Step 4: Repeat Steps 2–3 for `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`**

Same loop, same corruption check, substituting the test name.

- [ ] **Step 5: Rebuild and confirm both tests pass, for EF10**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_reference_when_entity_in_projection|FullyQualifiedName~NorthwindIncludeQueryMongoTest.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join"
```

Expected: PASS, 2/2 (or 4/4 counting `async: true/false` for each).

- [ ] **Step 6: Repeat Steps 1–5 for EF9 and EF8**

Same commands with `-c "Debug EF9"` then `-c "Debug EF8"`. Since this fix touches no `#if`-guarded code, expect
the identical outcome on all three versions.

- [ ] **Step 7: Commit the baseline changes**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindEFPropertyIncludeQueryMongoTest.cs
git commit -m "EF-322: regenerate Include_reference_when_entity_in_projection / Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join baselines"
```

---

### Task 5: Full regression sweep across EF8/EF9/EF10

**Files:** none — verification only.

- [ ] **Step 1: `MONGODB_EF_NATIVE_ONLY=1` full `~Query` sweep, EF10**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query" --logger "console;verbosity=normal" > /tmp/ef10_include_materialization_nativeonly.txt 2>&1
tail -8 /tmp/ef10_include_materialization_nativeonly.txt
grep "^  Failed" /tmp/ef10_include_materialization_nativeonly.txt | grep -oE "[A-Za-z0-9_]+Test\.[A-Za-z0-9_]+" | sort -u
```

Expected failure set: `Include_with_complex_projection_does_not_change_ordering_of_projection` (Root Cause B,
out of scope) plus the same pre-existing, unrelated `NorthwindMiscellaneousQueryMongoTest` failures already
known from the prior plan's own final report (`Contains_over_concatenated_columns_both_fixed_length`,
`OrderBy_object_type_server_evals`, `Projection_skip_projection`, `Projection_skip_take_projection`,
`Projection_take_projection`). Any NEW name beyond that known set is a regression — STOP and use
`superpowers:systematic-debugging`.

- [ ] **Step 2: Repeat Step 1 for EF9 and EF8**

Same commands with `-c "Debug EF9"` then `-c "Debug EF8"`. Expect the identical failure-set delta.

- [ ] **Step 3: Full three-version regular (non-NativeOnly) test-all run**

```bash
/test-all
```

Expected: green across EF8, EF9, and EF10 for the full solution (unit, functional, and specification test
projects), excluding the same known pre-existing gaps.

- [ ] **Step 4: Final report**

Summarize for your human partner or the branch reviewer: confirmation that both target tests
(`Include_reference_when_entity_in_projection`, `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`)
pass under `NativeOnly` with correct data on all three EF versions; whether Task 1 Step 6's alias-collision
mitigation was needed in practice or not (and why); confirmation that the non-join-scope wrapped-reference-Include
data gap found during the original investigation (`Orders.Include(o => o.Owner).Select(o => new { o,
o.OwnerId })`, `Owner` materializing empty) is UNCHANGED by this plan — still present, still out of scope, still
needing its own separate ticket. No commit for this task — it's verification only.
