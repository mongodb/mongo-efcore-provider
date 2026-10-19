# Native chained-join scalar/computed projection leaves Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a wrapped `Select` trailing a 2+-join native chain go native when every leaf is a scalar or
computed value rooted at exactly one scope in the chain (e.g.
`.Join(...).Join(...).Select(x => new { a = x.Outer.Outer.Foo, b = x.Outer.Inner.Bar, c = x.Inner.Baz })`) —
today this unconditionally falls back to driver-LINQ.

**Architecture:** Extract the existing root-scope-only rerooting logic in
`NativeJoinScopeTranslator.TryTranslateRootScopeOnly` into a shared helper, then add a sibling entry point
(`TryTranslateSingleScope`) that accepts a leaf resolving to **any** single chain scope (not just the root)
and builds the matching translator (the existing single-scope constructor for the root, the existing
two-scope constructor — with an intentionally-unused outer parameter — for any Inner level). Wire this into
`NativeJoinScopeProjectionBinder`'s ordinary-leaf arm, replacing its unconditional decline for
`scope.Levels.Count > 1`.

**Tech Stack:** C#/.NET, EF Core provider internals (`src/MongoDB.EntityFrameworkCore/Query/`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-18-native-chained-join-scalar-projection-design.md`

## Global Constraints

- Multi-EF targeting: code under `src/` must build clean under EF8/EF9/EF10 (`Debug EF8`/`EF9`/`EF10`
  configurations). Nothing in this plan touches an EF-version-conditional API, so no new `#if` guards are
  expected — verify this assumption in Task 6.
- Scope resolution must always be by parameter identity (`ReferenceEquals`), never by member name — see
  `Query/AGENTS.md`'s durable invariant.
- This plan does **not** attempt cross-scope computed leaves (a leaf combining fields from two different
  chain scopes) — those must keep declining (see the spec's Scope/Out section). Do not widen
  `MongoTransparentScopeResolver.ScopeRerootingVisitor`'s `CrossScope` rejection to "fix" this.
- This plan does **not** touch `NativeSlotPopulator`'s post-confirmed-join slot-operator guard
  (`HasConfirmedJoinLookup && IsSevenSlotOperator`). `Skip`/`Take`/`Where`/`OrderBy` composed *after* the
  chain-scalar `Select` this plan enables must keep declining — that is a separate, follow-up ticket. Do not
  relax that guard here even if it looks like it would "complete" the motivating spec test — it would not be
  backed by this plan's own hazard analysis.
- `MONGODB_EF_NATIVE_ONLY=1` is the only reliable "did this actually go native" signal — MQL-shape assertions
  under default `Native` mode do not distinguish native from a structurally-identical fallback pipeline (see
  `Query/AGENTS.md`, Common Pitfalls).

---

## File structure

- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs` — extract
  `TryRerootToSingleScope`; add `TryTranslateSingleScope`.
- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs` — wire
  `TryTranslateSingleScope` into the ordinary-leaf arm for `scope.Levels.Count > 1`.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — new end-to-end
  `NativeOnly` cases.
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs`
  — new cases for `TryTranslateSingleScope`; confirm existing `TryTranslateRootScopeOnly` cases still pass
  after the refactor.
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs`
  — new cases for the widened ordinary-leaf arm.
- Modify `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — capability note (append only).

---

### Task 1: Pin current behavior with a failing `NativeOnly` test

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `MongoQueryMode.NativeOnly`, `CreateContext(seed, mode, name)`, `SeedOwnersOrdersAndLines()` (the
  existing three-source seed helper this file already has — used by
  `Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly` and
  `Chained_join_Where_terminal_Count_goes_native_under_NativeOnly` a few hundred lines up in the same file;
  do not add a new seed helper).
- Produces: a red test
  (`Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly`) that Tasks 2-4 turn green. No
  downstream task depends on new production symbols existing yet.

- [ ] **Step 1: Write the failing test**

Add this to `NativeJoinTests.cs`, near the other `Chained_join_*` tests (e.g. right after
`Chained_join_Where_terminal_Count_goes_native_under_NativeOnly`):

```csharp
[Fact]
public void Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly()
{
    // Native-chained-join-scalar-projection plan (2026-09-18). Every leaf here is a scalar value rooted at
    // exactly one scope in the chain: e.o.Name is the root (scope 0), e.r.Total is join #1's Inner (scope
    // 1), l.Sku is join #2's Inner (scope 2). No Skip/Take after the Select — that is a SEPARATE,
    // still-open gap (NativeSlotPopulator's post-confirmed-join guard), not part of this plan.
    var seed = SeedOwnersOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly));

    var results = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
        .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
        {
            OwnerName = e.o.Name,
            OrderTotal = e.r.Total,
            LineSku = l.Sku
        })
        .Where(x => x.OwnerName == seed.Owners[0].Name)
        .ToList();

    Assert.NotEmpty(results);
    Assert.All(results, r => Assert.Equal(seed.Owners[0].Name, r.OwnerName));
}
```

Note the `Where` is composed **after** the `Select` here, filtering on the *projected alias*
(`x.OwnerName`), not the join scope — this is a different, already-supported shape
(`NativeSlotPopulator`'s Distinct-alias-scope carve-out does not apply here; a plain post-projection filter
over a flattened `$project` output is unaffected by this plan either way). If Task 1's test turns out to
still fail for a reason OTHER than the projection binder's `Levels.Count > 1` decline once Tasks 2-4 land,
stop and re-diagnose via `superpowers:systematic-debugging` before changing this test to work around it.

- [ ] **Step 2: Run it and confirm it fails for the expected reason**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly"
```

Expected: FAIL with `NativeTranslationNotSupportedException: Query projects a non-entity result`. This
confirms the plan starts from the documented red state (the projection binder's `Levels.Count > 1` decline),
not some other unrelated failure.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: pin chained-join scalar-leaf projection as currently falling back to driver-LINQ"
```

---

### Task 2: Extract `TryRerootToSingleScope` (pure refactor, zero behavior change)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs`

**Interfaces:**
- Consumes: `MongoTransparentScopeResolver.ScopeRerootingVisitor`, the existing private
  `ReferencesParameterOutsideHopChain`/`ParameterPresenceVisitor` helpers (already in this file, backing
  `TryTranslateRootScopeOnly`'s parity guard — reused as-is, not duplicated).
- Produces: `private static bool TryRerootToSingleScope(MongoJoinScope scope, ParameterExpression rootParam,
  Expression body, out int scopeIndex, out Expression? rewritten)`. `TryTranslateRootScopeOnly`'s own body is
  rewritten to call it; its public signature and behavior are unchanged. Task 3 is the first NEW caller.

- [ ] **Step 1: Add the helper and rewrite `TryTranslateRootScopeOnly` to use it**

In `NativeJoinScopeTranslator.cs`, add the private helper (place it right before
`TryTranslateRootScopeOnly`, which becomes its first caller):

```csharp
/// <summary>
/// Rewrites <paramref name="body"/>'s <c>Outer</c>/<c>Inner</c> hop chain (rooted at <paramref
/// name="rootParam"/>) onto one synthetic parameter per <paramref name="scope"/> level, exactly as <see
/// cref="MongoTransparentScopeResolver.ScopeRerootingVisitor"/> does for <c>SelectMany</c>'s own chained
/// scopes. Succeeds only when the WHOLE body resolves to a SINGLE scope index (no <c>CrossScope</c>) and no
/// reference to <paramref name="rootParam"/> survives the rewrite outside that hop chain. Callers judge
/// whether the returned <paramref name="scopeIndex"/> is one they accept — this helper itself has no
/// opinion on which index is valid, matching <see cref="MongoTransparentScopeResolver.TryResolveScopeDepth"/>'s
/// own division of labor. See docs/superpowers/specs/2026-09-18-native-chained-join-scalar-projection-design.md.
/// </summary>
private static bool TryRerootToSingleScope(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body,
    out int scopeIndex, [NotNullWhen(true)] out Expression? rewritten)
{
    scopeIndex = -1;
    rewritten = null;

    // Carries forward TryTranslateRootScopeOnly's own "Final-review fix (M2)" guard — do not drop it in this
    // extraction. See that method's existing comment (being moved here) for why it's needed.
    if (!rootParam.Type.IsTransparentIdentifierType())
    {
        return false;
    }

    var sourceCount = scope.Levels.Count;

    var scopeParams = new ParameterExpression[sourceCount + 1];
    scopeParams[0] = Expression.Parameter(scope.OuterEntityType.ClrType, "rootScope");
    for (var i = 0; i < sourceCount; i++)
    {
        scopeParams[i + 1] = Expression.Parameter(scope.Levels[i].InnerEntityType.ClrType, $"innerScope{i}");
    }

    var visitor = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
        rootParam, hopNames: ["Outer", "Inner"], sourceCount, scopeParams);
    var candidate = visitor.Visit(body);

    if (visitor.CrossScope
        || visitor.ResolvedScope is not { } resolved
        || ReferencesParameterOutsideHopChain(candidate, rootParam))
    {
        return false;
    }

    scopeIndex = resolved;
    rewritten = candidate;
    return true;
}
```

Replace the existing `TryTranslateRootScopeOnly` body with:

```csharp
public static bool TryTranslateRootScopeOnly(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;

    if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten) || scopeIndex != 0)
    {
        return false;
    }

    var translator = new MongoExpressionTranslator(scope.OuterEntityType);
    return valueMode
        ? translator.TryTranslateValue(rewritten, out result)
        : translator.TryTranslate(rewritten, out result);
}
```

This deletes the old inline `IsTransparentIdentifierType` check, `rootScopeParam`/`scopeParams`
construction, and the standalone `ScopeRerootingVisitor`/`CrossScope`/`ResolvedScope`/
`ReferencesParameterOutsideHopChain` calls that used to live directly in `TryTranslateRootScopeOnly` — the
helper now owns all of that (including the "Final-review fix (M2)" `IsTransparentIdentifierType` guard,
moved verbatim into `TryRerootToSingleScope` in Step 1 above — **do not drop it**, it is a real, previously-
fixed guard, not dead code). Remove the duplicate from `TryTranslateRootScopeOnly` rather than leaving it
beside the new helper.

- [ ] **Step 2: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 3: Run the existing `NativeJoinScopeTranslatorTests` suite — must be byte-for-byte unchanged**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinScopeTranslatorTests"
```
Expected: PASS, identical pass set to before this task — every `TryTranslateRootScopeOnly_*` test in
particular (this task changed that method's implementation, not its contract).

- [ ] **Step 4: Run the full join/slot-populator/projection-binder suites for a broader regression check**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"
```
Expected: PASS, zero regressions (this task touched no production caller of `TryTranslateRootScopeOnly`
other than itself).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs
git commit -m "refactor: extract TryRerootToSingleScope from TryTranslateRootScopeOnly (no behavior change)"
```

---

### Task 3: `NativeJoinScopeTranslator.TryTranslateSingleScope`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs`

**Interfaces:**
- Consumes: `TryRerootToSingleScope` (Task 2), the existing two-argument and four-argument
  `MongoExpressionTranslator` constructors (`MongoExpressionTranslator.cs:67` and `:87`).
- Produces: `NativeJoinScopeTranslator.TryTranslateSingleScope(MongoJoinScope scope, ParameterExpression
  rootParam, Expression body, bool valueMode, out MongoExpression? result)` — Task 4's only new caller.

- [ ] **Step 1: Write the failing unit tests**

Add to `NativeJoinScopeTranslatorTests.cs`, after the existing `TryTranslateRootScopeOnly_*` tests (reuse
`NewTwoLevelScope()`/`NewChainedRootParam()` already defined in that file — do not redefine them):

```csharp
// ── TryTranslateSingleScope: any single scope in a chain, not just the root ─────────────────────────────

[Fact]
public void TryTranslateSingleScope_translates_a_root_scoped_value_over_a_two_level_chain()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Outer.Outer.Name — root (scope 0): must resolve unprefixed, same as TryTranslateRootScopeOnly.
    Expression body = Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name");

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.True(translated);
    var field = Assert.IsType<MongoFieldExpression>(result);
    Assert.Equal("Name", field.ElementName);
}

[Fact]
public void TryTranslateSingleScope_translates_the_first_joins_inner_field_prefixed()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Outer.Inner.Total — the FIRST join's Inner side (scope 1).
    Expression body = Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Inner"), "Total");

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.True(translated);
    var field = Assert.IsType<MongoFieldExpression>(result);
    Assert.Equal(InnerPrefix + ".Total", field.ElementName);
}

[Fact]
public void TryTranslateSingleScope_translates_the_second_joins_inner_field_prefixed()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Inner.Label — the SECOND (outermost) join's Inner side (scope 2).
    Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label");

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.True(translated);
    var field = Assert.IsType<MongoFieldExpression>(result);
    Assert.Equal("_lookup_Other.Label", field.ElementName);
}

[Fact]
public void TryTranslateSingleScope_translates_a_computed_expression_within_one_level()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Outer.Inner.Total + 1 — computed, but still rooted at exactly ONE scope (level 1).
    Expression body = Expression.Add(
        Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Inner"), "Total"),
        Expression.Constant(1));

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.True(translated);
    var binary = Assert.IsType<MongoBinaryExpression>(result);
    var left = Assert.IsType<MongoFieldExpression>(binary.Left);
    Assert.Equal(InnerPrefix + ".Total", left.ElementName);
}

[Fact]
public void TryTranslateSingleScope_declines_a_leaf_mixing_root_and_a_joins_inner_scope()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Outer.Outer.Name.Length + x.Inner.Label.Length — spans scope 0 AND scope 2. Use string.Length
    // (an int-returning member, not a method call) so this is purely an arithmetic-over-two-scopes shape,
    // not confounded by a separate "method calls aren't translatable" decline reason.
    Expression body = Expression.Add(
        Expression.PropertyOrField(
            Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name"),
            "Length"),
        Expression.PropertyOrField(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label"), "Length"));

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.False(translated);
    Assert.Null(result);
}

[Fact]
public void TryTranslateSingleScope_declines_a_shape_the_underlying_translator_cannot_handle()
{
    var scope = NewTwoLevelScope();
    var x = NewChainedRootParam();

    // x => x.Inner.Label.ToUpper() — resolves to a single scope (2), but ToUpper() has no query-dialect
    // equivalent (mirrors NativeJoinScopeTranslatorTests.Declines_a_shape_the_underlying_translator_cannot_handle).
    var label = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label");
    var toUpper = typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!;
    Expression body = Expression.Call(label, toUpper);

    var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

    Assert.False(translated);
    Assert.Null(result);
}
```

- [ ] **Step 2: Run and confirm they fail (method doesn't exist yet)**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~TryTranslateSingleScope"
```
Expected: build error (`TryTranslateSingleScope` doesn't exist on `NativeJoinScopeTranslator`).

- [ ] **Step 3: Implement**

Add to `NativeJoinScopeTranslator.cs`, alongside `TryTranslateRootScopeOnly`:

```csharp
/// <summary>
/// Resolves a scalar/computed leaf rooted at ANY SINGLE scope in a chained join — the root, or any one
/// join's Inner side — never a leaf that spans more than one scope (see <see cref="TryRerootToSingleScope"/>'s
/// <c>CrossScope</c> rejection). Used by <see cref="NativeJoinScopeProjectionBinder"/>'s ordinary-leaf arm
/// once <c>scope.Levels.Count &gt; 1</c>, in place of the flat, type-comparing depth-1 entry points
/// (<see cref="TryTranslateValue"/>/<see cref="TryTranslatePredicate"/>), whose own documented RESIDUAL GAP is
/// specifically about chains. This method never falls into that gap: resolution is by the ACTUAL member-name
/// hop chain (<see cref="MongoTransparentScopeResolver"/>), never by comparing a scope's recorded CLR type
/// against some OTHER level's type. See
/// docs/superpowers/specs/2026-09-18-native-chained-join-scalar-projection-design.md.
/// </summary>
public static bool TryTranslateSingleScope(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten))
    {
        return false;
    }

    MongoExpressionTranslator translator;
    if (scopeIndex == 0)
    {
        translator = new MongoExpressionTranslator(scope.OuterEntityType);
    }
    else
    {
        var level = scope.Levels[scopeIndex - 1];

        // Reuse the two-scope MongoExpressionTranslator constructor with a THROWAWAY outer parameter that
        // never appears in `rewritten` (TryRerootToSingleScope already proved the whole body resolves to
        // scopeIndex, never scope 0) — so every member in `rewritten` resolves via the "not outer" branch:
        // scope.Levels[k-1].InnerEntityType, prefixed with scope.Levels[k-1].InnerPrefix. See this method's
        // own design doc for why this is safe, not a hack.
        var unusedOuterParam = Expression.Parameter(scope.OuterEntityType.ClrType, "unusedOuterScope");
        translator = new MongoExpressionTranslator(
            level.InnerEntityType, unusedOuterParam, scope.OuterEntityType, level.InnerPrefix);
    }

    return valueMode
        ? translator.TryTranslateValue(rewritten, out result)
        : translator.TryTranslate(rewritten, out result);
}
```

- [ ] **Step 4: Run tests again**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~TryTranslateSingleScope"
```
Expected: PASS for all six.

- [ ] **Step 5: Re-run the full `NativeJoinScopeTranslatorTests` file for regressions**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinScopeTranslatorTests"
```
Expected: PASS, zero regressions.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeTranslatorTests.cs
git commit -m "feat: NativeJoinScopeTranslator.TryTranslateSingleScope for chain-scalar projection leaves"
```

---

### Task 4: Wire `TryTranslateSingleScope` into `NativeJoinScopeProjectionBinder`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs`

**Interfaces:**
- Consumes: `NativeJoinScopeTranslator.TryTranslateSingleScope` (Task 3).
- Produces: `TryBindProjection` now stages a scalar/computed leaf resolving to any single chain scope
  instead of declining the whole projection whenever `scope.Levels.Count > 1`.

- [ ] **Step 1: Write the failing unit tests**

Add to `NativeJoinScopeProjectionBinderTests.cs`, after
`Binds_a_two_level_chain_projection_naming_every_scope_as_a_whole_entity` (reuse the existing `ChainOwner`/
`ChainOrder`/`ChainOrderLine`/`TranslateThreeSourceJoinQuery` fixtures already defined in that file — do not
add new ones):

```csharp
[Fact]
public void Binds_a_two_level_chain_projection_with_scalar_leaves_at_every_scope()
{
    // Native-chained-join-scalar-projection plan (2026-09-18). Every leaf is a plain scalar rooted at
    // exactly one scope: e.o.Name (root, scope 0), e.r.Total (join #1's Inner, scope 1), l.Sku (join #2's
    // Inner, scope 2). Mirrors the motivating shape in
    // NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties, minus the
    // trailing Skip/Take (a separate, still-open gap).
    var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
        owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            }));

    Assert.NotNull(mongoQ.Select.JoinScope);
    var scope = mongoQ.Select.JoinScope!;
    Assert.Equal(2, scope.Levels.Count);

    Assert.Equal(["OwnerName", "OrderTotal", "LineSku"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

    // OwnerName (root, scope 0): plain MongoFieldExpression, unprefixed.
    var ownerName = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[0].Expression);
    Assert.Equal("Name", ownerName.ElementName);

    // OrderTotal (join #1's Inner, scope 1): prefixed with Levels[0].InnerPrefix.
    var orderTotal = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
    Assert.Equal(scope.Levels[0].InnerPrefix + ".Total", orderTotal.ElementName);

    // LineSku (join #2's Inner, scope 2): prefixed with Levels[1].InnerPrefix.
    var lineSku = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[2].Expression);
    Assert.Equal(scope.Levels[1].InnerPrefix + ".Sku", lineSku.ElementName);

    Assert.Equal(2, mongoQ.Lookups.Count);
    Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
    Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
}

[Fact]
public void Binds_a_chain_scalar_leaf_mixed_with_a_whole_entity_leaf()
{
    // `new { cr = e.o, or = e.r, LineSku = l.Sku }` — a whole-entity leaf (root) alongside a chain-scalar
    // leaf (scope 2). The existing whole-entity sibling-readability guard already accepts a plain
    // MongoFieldExpression sibling; this leaf produces exactly that, so no guard change is needed — this
    // test verifies that, not just the leaf's own translation.
    var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
        owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { cr = e.o, or = e.r, LineSku = l.Sku }));

    Assert.NotNull(mongoQ.Select.JoinScope);
    var scope = mongoQ.Select.JoinScope!;

    Assert.Equal(["cr", scope.Levels[0].InnerPrefix, "LineSku"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

    var lineSku = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[2].Expression);
    Assert.Equal(scope.Levels[1].InnerPrefix + ".Sku", lineSku.ElementName);

    Assert.Equal(2, mongoQ.Lookups.Count);
    Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
    Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
}

[Fact]
public void Declines_a_chain_scalar_leaf_that_spans_two_scopes()
{
    // `Mixed = e.r.Total + l.Id` spans scope 1 (Total) AND scope 2 (Id) in a single leaf — out of scope for
    // this plan (see the design doc's Scope/Out section). Must decline the WHOLE projection, not partially
    // commit the other, individually-translatable leaf.
    var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
        owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o.Name, Mixed = e.r.Total + l.Id }));

    Assert.NotNull(mongoQ.Select.JoinScope);
    Assert.Empty(mongoQ.Select.Projection);
    Assert.Empty(mongoQ.Lookups);
    Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
}
```

- [ ] **Step 2: Run and confirm they fail**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Binds_a_two_level_chain_projection_with_scalar_leaves_at_every_scope|FullyQualifiedName~Binds_a_chain_scalar_leaf_mixed_with_a_whole_entity_leaf|FullyQualifiedName~Declines_a_chain_scalar_leaf_that_spans_two_scopes"
```
Expected: the first two FAIL (currently `NativeRoute.Fallback`/empty projection, since `Levels.Count > 1`
always declines the ordinary-leaf arm today); the third already PASSES (it declines today too, just for the
same blanket reason — confirm it stays passing after Step 3, for the RIGHT reason, not the old blanket one).

- [ ] **Step 3: Implement**

In `NativeJoinScopeProjectionBinder.cs`, find the "ORDINARY (scalar/computed) leaf" comment block near the
end of the `foreach (var (alias, leafBody) in members)` loop. Replace:

```csharp
if (scope.Levels.Count > 1)
{
    return false;
}

if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out var computedLeaf))
{
    return false; // one untranslatable leaf declines the whole projection — no partial commit
}
```

with:

```csharp
MongoExpression? computedLeaf;
if (scope.Levels.Count > 1)
{
    if (!NativeJoinScopeTranslator.TryTranslateSingleScope(scope, rootParam, leafBody, valueMode: true, out computedLeaf))
    {
        return false; // one untranslatable/cross-scope leaf declines the whole projection — no partial commit
    }
}
else if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out computedLeaf))
{
    return false; // one untranslatable leaf declines the whole projection — no partial commit
}
```

Leave everything below this block (the `seenAliases.Add(alias)` check and `staged.Add(new
MongoProjection(alias, computedLeaf))`) exactly as-is — `computedLeaf` is now assigned by either branch, so
the rest of the method compiles unchanged.

- [ ] **Step 4: Run the three tests again**

Run the same filter as Step 2.
Expected: all three PASS now.

- [ ] **Step 5: Run the full `NativeJoinScopeProjectionBinderTests` file for regressions**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinScopeProjectionBinderTests"
```
Expected: PASS, zero regressions — in particular
`Declines_a_second_chained_join_rather_than_reusing_the_first_joins_scope` must still decline (that shape's
leaves, `o.Name`/`r2.Total`, are single-hop off a FLAT, depth-1 `TransparentIdentifier` even though the
recorded `JoinScope` says `Levels.Count == 2` — `TryRerootToSingleScope`'s underlying
`MongoTransparentScopeResolver.TryResolveScopeDepth` requires the FULL hop-chain depth to be consumed for a
2-level scope, so a 1-hop access never resolves to any scope index at all and `ResolvedScope` stays `null` —
read that test's own comments before assuming this task changed its outcome).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs
git commit -m "feat: admit chain-scalar/computed projection leaves resolving to a single chain scope"
```

---

### Task 5: Flip Task 1's end-to-end test and add sibling functional coverage

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-4.
- Produces: Task 1's test passes; a companion test proves the still-open Skip/Take-after-join gap remains
  correctly declined (not accidentally "fixed" as a side effect — it should not be, since this plan never
  touches `NativeSlotPopulator`'s post-confirmed-join guard, but verify rather than assume).

- [ ] **Step 1: Run Task 1's test — now expected green**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly"
```
Expected: PASS.

- [ ] **Step 2: Add a test pinning that the ORIGINAL motivating shape (with Skip/Take) still declines**

Add to `NativeJoinTests.cs`, right after the test from Task 1:

```csharp
[Fact]
public void Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly()
{
    // The exact shape NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_
    // Properties has, minus Northwind's specific entities: a chain-scalar projection (this plan's own
    // feature) followed by Skip/Take. This plan does NOT touch NativeSlotPopulator's post-confirmed-join
    // guard, so paging after the newly-native projection must still decline — this is the gap a SEPARATE,
    // follow-up plan closes. If this test starts PASSING (i.e. Skip/Take goes native) without that follow-up
    // plan having landed, something in Tasks 2-4 leaked past the intended scope — stop and investigate via
    // superpowers:systematic-debugging rather than accepting the unexpected win.
    var seed = SeedOwnersOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly));

    Assert.Throws<NativeTranslationNotSupportedException>(() =>
        db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .Skip(1).Take(1)
            .ToList());
}
```

- [ ] **Step 3: Run it**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly"
```
Expected: PASS (the `Assert.Throws` succeeds).

- [ ] **Step 4: Run the full `NativeJoinTests` file for regressions**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"
```
Expected: PASS, zero regressions.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: flip chained-join scalar-leaf projection green; pin trailing-paging gap as still open"
```

---

### Task 6: Full multi-EF regression and documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: nothing new — this task is verification plus a documentation append.

- [ ] **Step 1: Run the full spec suite under `MONGODB_EF_NATIVE_ONLY=1` for EF10, diff the pass/fail counts**

Run:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build
```
Expected: zero `Passed -> Failed` flips anywhere.
`NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties` is expected to
**still fail** here — this plan does not fix it end-to-end (the trailing `Skip`/`Take` gap remains). If
anything else flips to `Failed`, bisect by reverting Task 4 first, then Task 3.

- [ ] **Step 2: Run the full functional + unit test suites for EF10**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`
Expected: PASS, zero regressions outside the tests this plan added.

- [ ] **Step 3: Invoke `/test-all` for full EF8/EF9/EF10 coverage**

Nothing in this plan touches an EF-version-conditional code path, so no `#if` divergence is expected —
confirm this rather than assume it.

- [ ] **Step 4: Update `Query/AGENTS.md`'s capability summary**

Add a sentence near the existing chained-join capability note (added by the `2026-09-07-native-chained-
join-scope` plan's own Task 9) noting that a trailing `Select` over a `Joins.Count >= 2` chain now also goes
native when every leaf is a scalar/computed value rooted at exactly ONE scope (not just whole-entity
leaves) — cross-reference this plan's spec doc — and that a leaf spanning more than one scope, or
`Skip`/`Take`/`Where`/`OrderBy` composed after such a projection, still fall back. Do not overwrite the
existing whole-entity-leaf documentation; append.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "docs: record native chained-join scalar-leaf projection support in Query/AGENTS.md"
```

---

## Explicitly out of scope (do not implement in this plan)

- A single leaf computed across MORE THAN ONE chain scope (e.g. `ca.CustomerID + cb.CustomerID`) — declines
  via `CrossScope`, by design; would need an N-scope-aware `MongoExpressionTranslator`, not attempted here.
- `Skip`/`Take`/`Where`/`OrderBy`/`ThenBy` composed after a chain-scalar projection `Select` — blocked by
  `NativeSlotPopulator`'s `HasConfirmedJoinLookup` guard, untouched by this plan. This is what keeps
  `Join_Customers_Orders_Orders_Skip_Take_Same_Properties` itself red even after this plan lands — a
  follow-up plan's job.
- A nested wrapped leaf (`X = new { Id = ca.CustomerID }`) over a chain — already separately gated to
  `Levels.Count == 1`, unaffected here.
- Widening per-join eligibility conjuncts — unchanged.
