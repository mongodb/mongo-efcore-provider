# Native Join/LeftJoin Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **SUPERSEDED (2026-08-26, post-merge):** this plan is historical record only. Tasks 1-8 were executed, merged, and finished with a clean final review — then, per a later maintainer decision, the dormant scaffolding from Tasks 2/3/5 (`MongoJoinScope`, `MongoJoinBinder`, the `MongoSelectLowerer` arm) was removed from the tree entirely rather than kept dormant. Task 1's `MongoTransparentScopeResolver` extraction remains live. See `docs/superpowers/specs/2026-08-26-native-join-translation-design.md`'s "Blocker found during implementation" section and EF-439 for the full account, including what a future re-implementation would need if EF Core's nav-expansion behavior ever changes.

**Goal:** Make a genuine (non-Include-sugar) `Join`/`LeftJoin` call — where the result selector combines fields from both the outer and inner sides — translate to a native `$lookup`+`$unwind` pipeline instead of always falling back to the driver-LINQ bridge.

**Architecture:** `TranslateJoinCore` (in `MongoQueryableMethodTranslatingExpressionVisitor.cs`) already resolves the join's navigation and builds a `JoinInfo` for every `Join`/`LeftJoin`/`GroupJoin`, but today only ever confirms an Include-shaped join as native (via the separate `TryConfirmReferenceInclude`, invoked from a later `Select`). This plan adds a new binder, `MongoJoinBinder`, invoked directly from `TranslateJoinCore` for a genuine two-sided join, which (1) registers a native `$lookup`/`$unwind` reusing the navigation `RebindInnerShaperToOuterQuery` already resolved, and (2) — only when the result selector captures a whole side (`(o, i) => new { o, i }`), not just already-flattened scalars — records a join scope so later `Where`/`OrderBy`/`Select` can resolve member access against either side by parameter identity. The per-node "does this field belong to scope A or scope B" mechanics reuse `MongoExpressionTranslator`'s existing two-scope constructor unchanged; what's genuinely new is a **generalized version of `NativeSelectManyBinder`'s scope-rerooting walker** (today hardcoded to the literal member names `"Outer"`/`"Inner"`), extracted so both SelectMany and Join can drive it with their own member-name-to-scope mapping.

**Tech Stack:** C# / .NET, EF Core (EF8/EF9/EF10 multi-targeted via build configuration), MongoDB C# driver, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-26-native-join-translation-design.md`

## Global Constraints

- Branch: `EF-322-Native-LINQ-rebased`. Every commit message is JIRA-numbered: `EF-392: <description>`.
- After every task, `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` must succeed with zero warnings-as-errors violations before committing. No EF8/EF9-conditional code is anticipated for this feature (no version-specific API surface is touched) — if a task turns out to need one, flag it rather than guessing.
- `<Nullable>enable</Nullable>` applies to all new `src/` code — annotate accordingly.
- No behavior change to any existing SelectMany shape is acceptable from the Task 1 refactor — `NativeSelectManyTests` (functional) and the SelectMany unit tests must produce byte-identical MQL before and after.
- GroupJoin's own array/grouped result shape, chained/nested joins, and any widening of `$lookup` eligibility constraints (query-filtered targets, composite non-PK keys, computed key selectors) are explicitly **out of scope** — declines to fallback, and each gets its own regression test proving the decline is deliberate (Task 6).
- A join with no resolvable model navigation (a pure key-equality join) is **out of scope for this chunk** — it declines to fallback. (`LookupExpression` has a navigation-less constructor for this case, added for EF-377's multi-hop flattening, but wiring it into `MongoJoinBinder` is deferred — note this explicitly in Task 3's decline list rather than silently mishandling it.)

---

### Task 1: Generalize `NativeSelectManyBinder`'s scope-rerooting walker into a shared resolver

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoTransparentScopeResolver.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs:860-921` (the `ScopeRerootingVisitor` class and `TryResolveScopeDepth` method)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoTransparentScopeResolverTests.cs` (new)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` (regression check only, no new tests required)

**Interfaces:**
- Produces:
  - `internal static bool TryResolveScopeDepth(Expression? scopeAccess, ParameterExpression rootParam, IReadOnlyList<string> hopNames, out int scopeIndex)` on `MongoTransparentScopeResolver` — generalizes the old fixed `"Outer"`/`"Inner"` walk to an arbitrary ordered list of hop member names (SelectMany passes the two-element list `["Outer", "Inner"]` repeatedly per nesting level, matching today's behavior exactly).
  - `internal sealed class ScopeRerootingVisitor(ParameterExpression rootParam, IReadOnlyList<string> hopNames, int sourceCount, ParameterExpression[] scopeParams) : ExpressionVisitor` — moved from `NativeSelectManyBinder`, same `ResolvedScope`/`CrossScope` public surface, now parameterized on `hopNames` instead of the literal strings.
- Consumes: nothing new — pure refactor of existing private members.

This task is deliberately small and lands as its own commit **before** any join-specific work, to minimize diff overlap with the concurrent EF-411 agent's changes to `MongoSelectDefinition.cs` (post-terminal guards) — this task does not touch that file at all.

- [ ] **Step 1: Read the current `TryResolveScopeDepth` and `ScopeRerootingVisitor` to confirm the exact behavior being preserved**

Already confirmed by reading `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs:860-921`. The walk today does: peel a chain of `MemberExpression { Member.Name: "Outer" or "Inner" }` down to the bare `ti` parameter, reverse it, and classify as either `sourceCount` trailing-`"Outer"` hops (root, scope 0) or `(sourceCount - k)` leading `"Outer"` hops followed by one trailing `"Inner"` (scope `k`).

- [ ] **Step 2: Write a failing unit test for the generalized resolver**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoTransparentScopeResolverTests.cs
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoTransparentScopeResolverTests
{
    private sealed class Ti
    {
        public object Outer { get; set; } = null!;
        public object Inner { get; set; } = null!;
    }

    [Fact]
    public void Resolves_root_scope_for_pure_Outer_chain_matching_source_count()
    {
        var ti = Expression.Parameter(typeof(Ti), "ti");
        Expression access = Expression.Property(Expression.Property(ti, "Outer"), "Outer");

        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            access, ti, hopNames: ["Outer", "Inner"], sourceCount: 2, out var scopeIndex);

        Assert.True(resolved);
        Assert.Equal(0, scopeIndex);
    }

    [Fact]
    public void Resolves_inner_scope_for_trailing_Inner_hop()
    {
        var ti = Expression.Parameter(typeof(Ti), "ti");
        Expression access = Expression.Property(Expression.Property(ti, "Outer"), "Inner");

        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            access, ti, hopNames: ["Outer", "Inner"], sourceCount: 2, out var scopeIndex);

        Assert.True(resolved);
        Assert.Equal(1, scopeIndex);
    }

    [Fact]
    public void Supports_arbitrary_hop_names_for_a_join_scope()
    {
        var x = Expression.Parameter(typeof(Ti), "x");
        Expression access = Expression.Property(x, "Outer"); // reuse Ti shape; real Join uses caller-chosen names

        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            access, x, hopNames: ["Outer"], sourceCount: 1, out var scopeIndex);

        Assert.True(resolved);
        Assert.Equal(1, scopeIndex);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails (type doesn't exist yet)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoTransparentScopeResolverTests"`
Expected: build error, `MongoTransparentScopeResolver` does not exist.

- [ ] **Step 4: Create `MongoTransparentScopeResolver` by lifting the two members out of `NativeSelectManyBinder`, generalized on `hopNames`**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoTransparentScopeResolver.cs
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

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Shared parameter-identity scope resolution for a nested "transparent identifier"-shaped parameter — a
/// single lambda parameter whose member-access chain (e.g. <c>ti.Outer.Inner</c>) walks down through a fixed
/// set of named hops to one of several logical scopes. Originally built for <c>SelectMany</c>'s
/// <c>TransparentIdentifier(Outer, Inner)</c> shape (hop names always literally <c>"Outer"</c>/<c>"Inner"</c>);
/// generalized to a caller-supplied <paramref name="hopNames"/> list so <c>Join</c>'s own (possibly
/// user-named) two-scope result selector can reuse the identical walk. See the scope-by-parameter-identity
/// invariant in <c>Query/AGENTS.md</c> — resolution is always by parameter identity, never by member name.
/// </summary>
internal static class MongoTransparentScopeResolver
{
    /// <summary>
    /// Peels a chain of member accesses named from <paramref name="hopNames"/> down to the bare
    /// <paramref name="rootParam"/> parameter, and resolves which scope it refers to. Given
    /// <paramref name="sourceCount"/> chained scopes, the <c>k</c>-th level's own element is reached via
    /// <c>(sourceCount - k)</c> leading <c>hopNames[0]</c> hops followed by exactly one trailing
    /// <c>hopNames[1]</c> hop; the root scope is reached via exactly <paramref name="sourceCount"/>
    /// <c>hopNames[0]</c> hops and no <c>hopNames[1]</c> at all. <paramref name="scopeIndex"/> is <c>0</c> for
    /// the root, or <c>k</c> (1-based) for the <c>k</c>-th nested scope. Returns <see langword="false"/> —
    /// declining cleanly — for any chain that does not terminate exactly at <paramref name="rootParam"/>, is
    /// empty, exceeds <paramref name="sourceCount"/> hops, or does not match either valid shape.
    /// </summary>
    internal static bool TryResolveScopeDepth(
        Expression? scopeAccess, ParameterExpression rootParam, IReadOnlyList<string> hopNames, int sourceCount,
        out int scopeIndex)
    {
        scopeIndex = -1;
        var outerHop = hopNames[0];
        var innerHop = hopNames[1];

        var path = new List<string>();
        var current = scopeAccess;
        while (current is MemberExpression { Member.Name: { } name } hop && (name == outerHop || name == innerHop))
        {
            path.Add(name);
            current = hop.Expression;
        }

        if (current != rootParam || path.Count == 0 || path.Count > sourceCount)
            return false;

        path.Reverse(); // now ordered outward-from-root: path[0] is the first hop off rootParam.

        if (path[^1] == innerHop && path.Take(path.Count - 1).All(h => h == outerHop))
        {
            scopeIndex = sourceCount - path.Count + 1;
            return true;
        }

        if (path.Count == sourceCount && path.All(h => h == outerHop))
        {
            scopeIndex = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rewrites every scope-rooted member access in an expression onto the matching per-scope synthetic
    /// parameter, recording the single scope it resolves to (or flagging <see cref="CrossScope"/> if operands
    /// span more than one). A non-scope-rooted member is left untouched.
    /// </summary>
    internal sealed class ScopeRerootingVisitor(
        ParameterExpression rootParam, IReadOnlyList<string> hopNames, int sourceCount, ParameterExpression[] scopeParams)
        : ExpressionVisitor
    {
        public int? ResolvedScope { get; private set; }
        public bool CrossScope { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryResolveScopeDepth(node.Expression, rootParam, hopNames, sourceCount, out var scope))
            {
                if (ResolvedScope is { } prior && prior != scope)
                    CrossScope = true;
                ResolvedScope = scope;
                return Expression.MakeMemberAccess(scopeParams[scope], node.Member);
            }

            return base.VisitMember(node);
        }
    }
}
```

- [ ] **Step 5: Update `NativeSelectManyBinder` to delegate to the shared resolver instead of its own private copies**

Replace the private `TryResolveScopeDepth` method (lines 892-921) and the private `ScopeRerootingVisitor` class (lines 860-878) entirely — delete them. Update every call site in `NativeSelectManyBinder.cs` that referenced them:

```csharp
// Every call like:
//   TryResolveScopeDepth(member.Expression, ti, sources.Count, out var scopeIndex)
// becomes:
MongoTransparentScopeResolver.TryResolveScopeDepth(
    member.Expression, ti, hopNames: ["Outer", "Inner"], sources.Count, out var scopeIndex)

// Every construction of the old ScopeRerootingVisitor like:
//   var scopes = new ScopeRerootingVisitor(ti, sources.Count, scopeParams);
// becomes:
var scopes = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
    ti, hopNames: ["Outer", "Inner"], sources.Count, scopeParams);
```

Apply this at all four call sites: `TryBindTransparentIdentifierProjection`'s member loop, `TryTranslateCrossScopeComputedLeaf`, `TryTranslateScopedOperand`, and `TryTranslateScopedSubtree`.

- [ ] **Step 6: Run the new unit test and the full SelectMany test suites to confirm zero behavior change**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoTransparentScopeResolverTests|FullyQualifiedName~NativeSelectManyBinderTests"`
Expected: PASS, all green.

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests"`
Expected: PASS, all green, identical MQL to before (this is a pure refactor).

- [ ] **Step 7: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoTransparentScopeResolver.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoTransparentScopeResolverTests.cs
git commit -m "EF-392: extract MongoTransparentScopeResolver from NativeSelectManyBinder for reuse by Join"
```

---

### Task 2: Add a join-scope marker to `MongoSelectDefinition`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs:394` (`HasTerminalOperator`) and add a new property/setter near the existing `UnwindSources`/`UnwindSource` block (`MongoSelectDefinition.cs:460-480`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoSelectDefinitionTests.cs` (add cases; if this file doesn't exist yet, check `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/` for the closest existing sibling — e.g. `MongoSelectDefinitionTrailingOpsTests.cs` — and follow its class/fixture pattern)

**Interfaces:**
- Produces:
  - `internal sealed class MongoJoinScope(string? outerMemberName, string? innerMemberName, IEntityType outerEntityType, IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)` — records which named member (if any) of a join's result-selector anonymous type captures the whole outer/inner entity, so a later operator's lambda can resolve `x.<name>.<Foo>` by parameter identity through the shared `MongoTransparentScopeResolver`. `outerMemberName`/`innerMemberName` are `null` when that side was not captured whole (already flattened to scalar leaves at Join time — no scope tracking needed for that side).
  - `internal MongoJoinScope? JoinScope { get; set; }` on `MongoSelectDefinition`.
  - `HasTerminalOperator` becomes `IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0 || JoinScope != null`.
- Consumes: nothing new (this task only adds state; Task 3 populates it).

- [ ] **Step 1: Write a failing unit test asserting `HasTerminalOperator` becomes true once `JoinScope` is set**

```csharp
// Add to (or create) tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoSelectDefinitionTests.cs
using Microsoft.EntityFrameworkCore.Metadata;
using Moq; // or the repo's existing IEntityType test-double pattern — check MongoSelectDefinitionTrailingOpsTests.cs
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class MongoSelectDefinitionJoinScopeTests
{
    [Fact]
    public void HasTerminalOperator_is_true_once_a_join_scope_is_set()
    {
        var select = new MongoSelectDefinition();
        Assert.False(select.HasTerminalOperator);

        var outerEntityType = Mock.Of<IEntityType>();
        var innerEntityType = Mock.Of<IEntityType>();
        select.JoinScope = new MongoJoinScope(
            outerMemberName: "o", innerMemberName: "i",
            outerEntityType, innerEntityType, innerPrefix: "_lookup_Orders", isLeftOuter: false);

        Assert.True(select.HasTerminalOperator);
    }
}
```

(If the constructor for `MongoSelectDefinition` isn't parameterless, check the actual constructor in `MongoSelectDefinition.cs` and adjust — other tests in the same directory show the real construction pattern to follow.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectDefinitionJoinScopeTests"`
Expected: build error, `MongoJoinScope` does not exist / `JoinScope` property does not exist.

- [ ] **Step 3: Create `MongoJoinScope`**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs
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

using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Records a native two-sided <c>Join</c>/<c>LeftJoin</c>'s scope shape, for a result selector that captures a
/// whole side (<c>(o, i) =&gt; new { o, i }</c>) rather than only already-flattened scalar leaves. When a side
/// is captured whole, a later <c>Where</c>/<c>OrderBy</c>/<c>Select</c> lambda's member access on that named
/// member (e.g. <c>x.o.Name</c>) must resolve through <see cref="MongoTransparentScopeResolver"/> against the
/// matching entity type, exactly like SelectMany's <c>ti.Outer</c>/<c>ti.Inner</c> — except the member names
/// here are whatever the user's own anonymous-type/DTO member names were, not fixed literals.
/// </summary>
internal sealed class MongoJoinScope(
    string? outerMemberName, string? innerMemberName,
    IEntityType outerEntityType, IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)
{
    /// <summary>The result-selector member name capturing the whole OUTER entity, or <see langword="null"/>
    /// if the outer side was already flattened to scalar leaves at Join time (no scope tracking needed).</summary>
    public string? OuterMemberName { get; } = outerMemberName;

    /// <summary>The result-selector member name capturing the whole INNER entity, or <see langword="null"/>
    /// if the inner side was already flattened to scalar leaves at Join time.</summary>
    public string? InnerMemberName { get; } = innerMemberName;

    public IEntityType OuterEntityType { get; } = outerEntityType;

    public IEntityType InnerEntityType { get; } = innerEntityType;

    /// <summary>The <c>$lookup</c> alias (<c>_lookup_&lt;Navigation&gt;</c>) inner-scope field refs are
    /// prefixed with — mirrors <see cref="MongoUnwindSource.InnerScopePath"/>.</summary>
    public string InnerPrefix { get; } = innerPrefix;

    /// <summary>Whether this join is left-outer (<c>LeftJoin</c>/<c>GroupJoin</c>) or inner (<c>Join</c>) —
    /// drives the emitted <c>$unwind</c>'s <c>preserveNullAndEmptyArrays</c>.</summary>
    public bool IsLeftOuter { get; } = isLeftOuter;
}
```

- [ ] **Step 4: Add the `JoinScope` property and extend `HasTerminalOperator` on `MongoSelectDefinition`**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`, near the existing `UnwindSources`/`UnwindSource` block (around line 466-480):

```csharp
/// <summary>The native two-sided join scope recorded by <c>MongoJoinBinder</c>, or <see langword="null"/>
/// if this select has no native join, or its join's result selector was already fully flattened to
/// scalar leaves (no whole-side capture, so no scope tracking is needed).</summary>
internal MongoJoinScope? JoinScope { get; set; }
```

And change line 394:

```csharp
internal bool HasTerminalOperator
    => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0 || JoinScope != null;
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectDefinitionJoinScopeTests"`
Expected: PASS.

- [ ] **Step 6: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoSelectDefinitionJoinScopeTests.cs
git commit -m "EF-392: add MongoJoinScope and extend HasTerminalOperator for native joins"
```

**Coordination note:** before this commit, run `git diff main -- src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (or the equivalent against whatever the EF-411 agent has landed on `main`/the shared branch by now) to check for a conflicting edit to `HasTerminalOperator` or the surrounding region, and rebase this one-line change on top if so, rather than force-merging.

---

### Task 3: `MongoJoinBinder` — eligibility, native `$lookup` registration, and scope classification

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoJoinBinderTests.cs` (new)

**Interfaces:**
- Consumes:
  - `MongoTransparentScopeResolver.TryResolveScopeDepth`/`ScopeRerootingVisitor` (Task 1).
  - `MongoJoinScope` (Task 2).
  - Existing `JoinInfo` (`Navigation`, `Alias`, `IsLeftOuter` — `src/MongoDB.EntityFrameworkCore/Query/Expressions/JoinInfo.cs`), `LookupExpression(INavigation, bool forceUnwind)` (`src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs:37`), `MongoQueryExpression.AddLookup` (`Expressions/MongoQueryExpression.Lookup.cs`), `MongoSelectDefinition.IsBareCollectionScan`/`IsGroupBy`/`IsDistinct` (existing).
- Produces:
  - `internal static bool TryBindJoin(MongoQueryExpression outerQueryExpression, MongoQueryExpression innerQueryExpression, LambdaExpression resultSelector, JoinInfo joinInfo)` — called from `TranslateJoinCore` (Task 4) immediately after `RebindInnerShaperToOuterQuery` returns a non-null shaper (i.e. the join itself is representable at all) and before the final `newResultSelector` substitution. Returns `false` (no mutation) for any shape outside this chunk's scope, matching the existing graceful-fallback contract for `Join` (never a hard decline — `Join` always has a working driver-LINQ bridge).

- [ ] **Step 1: Write failing unit tests for the two core decisions `MongoJoinBinder` must make: eligibility, and result-selector classification**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoJoinBinderTests.cs
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

// Uses this project's existing helpers for building a minimal IModel/IEntityType/MongoQueryExpression fixture —
// follow the construction pattern in NativeSelectManyBinderTests.cs (same directory) for the exact
// ModelBuilder/OnModelCreating setup this project already uses to get real IEntityType instances rather than
// mocking IEntityType by hand, since MongoExpressionTranslator reads real IProperty/IEntityType metadata.
public class MongoJoinBinderTests
{
    [Fact]
    public void Declines_when_outer_select_is_group_by()
    {
        // Arrange a MongoQueryExpression whose Select.IsGroupBy is true (mirrors the existing
        // TranslateJoinCore GroupBy-source guard already covered by NativeSelectManyBinder-adjacent tests).
        // Assert MongoJoinBinder.TryBindJoin returns false and mutates nothing (Select.JoinScope stays null).
    }

    [Fact]
    public void Declines_when_join_has_no_resolved_navigation()
    {
        // joinInfo.Navigation is null (key-equality join with no model navigation) — out of scope for this
        // chunk. Assert TryBindJoin returns false.
    }

    [Fact]
    public void Classifies_whole_side_capture_and_sets_JoinScope()
    {
        // resultSelector: (o, i) => new { o, i } — both parameters captured whole, by reference identity.
        // Assert TryBindJoin returns true, and Select.JoinScope.OuterMemberName == "o" &&
        // Select.JoinScope.InnerMemberName == "i".
    }

    [Fact]
    public void Already_flattened_result_selector_needs_no_join_scope()
    {
        // resultSelector: (o, i) => new { o.Name, i.Total } — no whole-side capture.
        // Assert TryBindJoin returns true, and Select.JoinScope stays null (nothing to track — every member
        // is already a resolved scalar leaf).
    }
}
```

(These are written as scaffolds with the real assertions and arrange-comments — fill in the exact `MongoQueryExpression`/`IEntityType` construction using this project's existing fixture-building helpers, which `NativeSelectManyBinderTests.cs` in the same directory already demonstrates; do not guess at a different construction pattern.)

- [ ] **Step 2: Run the tests to verify they fail (type doesn't exist yet)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoJoinBinderTests"`
Expected: build error, `MongoJoinBinder` does not exist.

- [ ] **Step 3: Implement `MongoJoinBinder`**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs
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

using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Binds a genuine two-sided <c>Join</c>/<c>LeftJoin</c> (a real relational join whose result selector
/// combines fields from both sides — NOT the Include-sugar shape <c>TryConfirmReferenceInclude</c> already
/// handles) to a native <c>$lookup</c>/<c>$unwind</c>. Scope for this first chunk: one join level, a
/// resolvable model navigation, a bare-collection-scan inner (the same single-level eligibility Include
/// already enforces). <see cref="TryBindJoin"/> returns <see langword="false"/> (no mutation) for anything
/// outside that scope — GroupJoin's own array-result shape, a chained/nested join, a navigation-less
/// key-equality join, a query-filtered or non-bare-scan inner — and the caller falls back to the existing
/// driver-LINQ bridge, which already produces correct results for all of these. See
/// <c>docs/superpowers/specs/2026-08-26-native-join-translation-design.md</c>.
/// </summary>
internal static class MongoJoinBinder
{
    internal static bool TryBindJoin(
        MongoQueryExpression outerQueryExpression,
        MongoQueryExpression innerQueryExpression,
        LambdaExpression resultSelector,
        JoinInfo joinInfo)
    {
        // Out of scope: GroupBy-sourced side (wrong-data hazard, already marked fallback-unsafe by the
        // caller) or Distinct-sourced side (correct-but-non-native fallback, already marked by the caller).
        // Both leave the query non-native via the caller's own MarkGroupByFallbackUnsafe/
        // MarkNotNativelyRepresentable calls — this binder must not try to additionally register a $lookup
        // on top of an already-declined select.
        if (outerQueryExpression.Select.IsGroupBy || innerQueryExpression.Select.IsGroupBy
            || outerQueryExpression.Select.IsDistinct || innerQueryExpression.Select.IsDistinct)
        {
            return false;
        }

        // Out of scope for this chunk: no resolved model navigation (a pure key-equality join with no
        // corresponding INavigation — LookupExpression's navigation-less constructor exists for EF-377's
        // multi-hop flattening, but wiring it into this binder is deferred).
        if (joinInfo.Navigation is not { } navigation)
        {
            return false;
        }

        // Single-level eligibility, mirroring TryConfirmReferenceInclude's own constraints: the inner side
        // must be a bare collection scan (no filter/projection/paging of its own) — widening this is
        // EF-368/chunk-B territory, not this chunk.
        if (!innerQueryExpression.Select.IsBareCollectionScan)
        {
            return false;
        }

        // Chained/nested joins are out of scope for this chunk: a join composed onto an already-joined
        // outer, or with a JoinScope already set from a prior join on this same select, declines here.
        if (outerQueryExpression.Select.JoinScope != null)
        {
            return false;
        }

        if (resultSelector.Parameters.Count != 2)
        {
            return false;
        }

        var outerParam = resultSelector.Parameters[0];
        var innerParam = resultSelector.Parameters[1];

        if (!TryReadMembers(resultSelector.Body, out var members))
        {
            return false;
        }

        string? outerMemberName = null;
        string? innerMemberName = null;

        foreach (var (alias, argExpr) in members)
        {
            if (ReferenceEquals(argExpr, outerParam))
            {
                if (outerMemberName != null) return false; // whole outer captured twice — ambiguous, decline
                outerMemberName = alias;
            }
            else if (ReferenceEquals(argExpr, innerParam))
            {
                if (innerMemberName != null) return false; // whole inner captured twice — ambiguous, decline
                innerMemberName = alias;
            }
            // A member that is neither param verbatim is an already-flattened scalar leaf (e.g. o.Name) —
            // it needs no scope tracking here; it is translated later by the ordinary single-scope path
            // exactly as any other projected Select member is, once this join is confirmed native below.
        }

        var lookup = new LookupExpression(navigation, forceUnwind: true)
        {
            PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
        };
        outerQueryExpression.AddLookup(lookup);

        if (outerMemberName != null || innerMemberName != null)
        {
            outerQueryExpression.Select.JoinScope = new MongoJoinScope(
                outerMemberName, innerMemberName,
                outerQueryExpression.CollectionExpression.EntityType, navigation.TargetEntityType,
                joinInfo.Alias, joinInfo.IsLeftOuter);
        }

        return true;
    }

    // new {...} (NewExpression with Members) or a parameterless MemberInit — mirrors the identical helper
    // already duplicated in NativeSelectManyBinder.TryReadProjection and NativeProjectionBinder. Not
    // extracted into a shared helper in this chunk (out of scope — flagged for a future cleanup pass rather
    // than bundled into this feature's diff).
    private static bool TryReadMembers(Expression body, out System.Collections.Generic.IReadOnlyList<(string Alias, Expression Arg)> members)
    {
        members = null!;
        var list = new System.Collections.Generic.List<(string, Expression)>();
        switch (body)
        {
            case NewExpression ne when ne.Members != null && ne.Members.Count == ne.Arguments.Count && ne.Arguments.Count > 0:
                for (var i = 0; i < ne.Arguments.Count; i++) list.Add((ne.Members[i].Name, ne.Arguments[i]));
                break;
            case MemberInitExpression mi when mi.NewExpression.Arguments.Count == 0 && mi.Bindings.Count > 0:
                foreach (var b in mi.Bindings)
                {
                    if (b is not MemberAssignment ma) return false;
                    list.Add((b.Member.Name, ma.Expression));
                }
                break;
            default:
                return false;
        }
        members = list;
        return true;
    }
}
```

- [ ] **Step 4: Run the unit tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoJoinBinderTests"`
Expected: PASS.

- [ ] **Step 5: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoJoinBinderTests.cs
git commit -m "EF-392: add MongoJoinBinder for native single-level Join/LeftJoin eligibility and scope classification"
```

---

### Task 4: Wire `MongoJoinBinder` into `TranslateJoinCore`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1630-1704` (`TranslateJoinCore`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (new — first smoke test only; the full suite is Task 7)

**Interfaces:**
- Consumes: `MongoJoinBinder.TryBindJoin` (Task 3).
- Produces: `TranslateJoinCore` now attempts native binding for every eligible join before falling through to its existing (unchanged) driver-LINQ-capable shaper substitution — native binding failure is invisible to the rest of the method, which proceeds exactly as before.

- [ ] **Step 1: Write a failing functional smoke test for the simplest native shape**

```csharp
// tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
/* Copyright 2023-present MongoDB Inc.
 * ... (standard header, copy from NativeSelectManyTests.cs)
 */

using System.Linq;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class NativeJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Follow the exact fixture/entity/CreateContext(WithLogging) helper pattern already established in
    // NativeSelectManyTests.cs in this same directory for constructing `db`, seeding, and reading back
    // MQL via spyLogger — do not invent a different harness.

    [Fact]
    public void Flattened_scalar_join_goes_native_with_correct_results_and_mql()
    {
        var seed = SeedOwnersAndOrders(); // one-time helper: two related entity sets with an FK
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Flattened_scalar_join_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md).
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"$lookup\"", message);
        Assert.Contains("\"$unwind\"", message);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: FAIL — throws `NativeTranslationNotSupportedException` under `NativeOnly` (today's "joins are not natively representable" decline).

- [ ] **Step 3: Wire the binder call into `TranslateJoinCore`**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, immediately after the existing `RebindInnerShaperToOuterQuery` call (around line 1694-1695) and before the `newResultSelector`/`return` lines:

```csharp
        var reboundInnerShaper = RebindInnerShaperToOuterQuery(
            inner.ShaperExpression, innerQueryExpression, outerQueryExpression, outerKeySelector, innerKeySelector, joinInfo);

        // NEW: attempt native binding for a genuine two-sided join (Include-shaped joins are confirmed
        // separately, later, by TryConfirmReferenceInclude — this call is a no-op for that shape since
        // TryBindJoin's result-selector classification only recognizes a NewExpression/MemberInit body,
        // never an IncludeExpression). A binding failure here is not a decline: the query still falls back
        // to the unchanged driver-LINQ bridge below via the ordinary Route == Fallback path, exactly as
        // before this task landed.
        MongoJoinBinder.TryBindJoin(outerQueryExpression, innerQueryExpression, resultSelector, joinInfo);

        var newResultSelector = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], outer.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], reboundInnerShaper!,
                resultSelector.Body));

        return outer.UpdateShaperExpression(newResultSelector);
```

Add the `using MongoDB.EntityFrameworkCore.Query.NativeTranslation;` directive at the top of the file if not already present (check first — `MongoQueryableMethodTranslatingExpressionVisitor.cs` already references other `NativeTranslation` types like `NativeSelectManyBinder`, so this using is very likely already there).

- [ ] **Step 4: Run the test — expect it to still fail, for a DIFFERENT reason**

Run the same command as Step 2.
Expected: `TryBindJoin` now returns `true` and registers the `$lookup`, but nothing downstream yet knows the select is natively representable for a join shape — `MongoSelectLowerer.AppendLookupStages`'s existing guard (`query.IsJoinQuery && lookups.Count < query.InnerCollections.Count`) still throws, because that guard doesn't yet know about this new lookup. This confirms Task 3's binder ran correctly; Task 5 closes the loop.

- [ ] **Step 5: Commit this step even though the smoke test doesn't fully pass yet — Tasks 5 and 6 complete the wiring**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-392: wire MongoJoinBinder into TranslateJoinCore"
```

(The build must pass even though the new functional test is still red — the sanity/build gate is about compilation and no regressions in *other* tests, not this in-progress feature's own not-yet-complete test. Re-run the untouched suites — `NativeSelectManyTests`, `NativeCastTests` or similar — as a spot-check that nothing else broke.)

---

### Task 5: Lower a bound join scope into `$lookup`/`$unwind` stages

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:342-395` (`AppendLookupStages`)

**Interfaces:**
- Consumes: `MongoSelectDefinition.JoinScope` (Task 2), existing `MongoQueryExpression.Lookups`/`IsJoinQuery`/`InnerCollections`.
- Produces: `AppendLookupStages` now recognizes a lowerable native-join `$lookup` (one whose `LookupExpression` is the one `MongoJoinBinder` registered) and emits `MongoLookupStage` + `MongoUnwindStage(preserveNullAndEmptyArrays: <join's own IsLeftOuter>)`, instead of only handling the `IsStreamableReference` (Include)/`IsNativeCollectionLookup`/`ForceUnwind`-navigation-collection branches already there.

- [ ] **Step 1: Re-run the Task 4 smoke test to confirm the exact current failure**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: FAIL with `NativeTranslationNotSupportedException: Native pipeline does not support this join shape (only single-level reference includes).` — this is the exact guard to fix.

- [ ] **Step 2: Fix `AppendLookupStages`'s join-coverage guard and add the join-scope branch**

The current code (`MongoSelectLowerer.cs:342-395`):

```csharp
    private static void AppendLookupStages(MongoQueryExpression query, List<MongoPipelineStage> stages)
    {
        var lookups = query.Lookups;

        // Join-coverage guard: if this is a join query and there are fewer lookups than inner
        // collections, emitting a partial pipeline would silently drop a join and return wrong results.
        if (query.IsJoinQuery && lookups.Count < query.InnerCollections.Count)
        {
            throw new NativeTranslationNotSupportedException(
                "Native pipeline does not support this join shape (only single-level reference includes).");
        }

        foreach (var lookup in lookups)
        {
            if (lookup.IsStreamableReference)
            {
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, lookup.PreserveNullAndEmptyArrays));
            }
            else if (lookup.IsNativeCollectionLookup)
            {
                stages.Add(new MongoLookupStage(lookup));
            }
            else if (lookup.Navigation is { IsCollection: true } && lookup.ForceUnwind)
            {
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: false));
            }
            else
            {
                throw new NativeTranslationNotSupportedException(/* ... */);
            }
        }
    }
```

This chunk's native join is registered as a `forceUnwind: true` lookup over a REFERENCE (non-collection, in the LINQ-`Join`-target sense — the navigation resolved from the outer key selector) navigation. Add a new arm that recognizes it — a `ForceUnwind` lookup whose navigation is a single (non-collection) reference, distinct from the existing `lookup.Navigation is { IsCollection: true } && lookup.ForceUnwind` arm (which is the reference-collection-`SelectMany`-flatten case, always inner-unwind). The join case must honor `MongoJoinScope.IsLeftOuter`/`joinInfo`'s left-outer-ness for its `preserveNullAndEmptyArrays`, not a fixed `false`:

```csharp
            else if (lookup.Navigation is { IsCollection: false } && lookup.ForceUnwind
                     && query.Select.JoinScope is { } joinScope && joinScope.InnerPrefix == lookup.As)
            {
                // A native two-sided Join/LeftJoin (MongoJoinBinder, EF-392): unwind semantics follow the
                // LINQ operator itself (Join => inner, LeftJoin/GroupJoin => left-outer), carried on
                // MongoJoinScope.IsLeftOuter — NOT a fixed default, unlike the reference-SelectMany-flatten
                // arm above (which is always inner-join by construction).
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: joinScope.IsLeftOuter));
            }
```

Insert this new arm **before** the existing `lookup.Navigation is { IsCollection: true } && lookup.ForceUnwind` arm in the `if`/`else if` chain (order matters only in that both are `ForceUnwind`-gated but discriminate on `IsCollection`, so either order is actually safe here — but placing the join arm first keeps the two `ForceUnwind` arms visually adjacent for a future reader).

Note this arm only fires when `JoinScope` is set — Task 3's `MongoJoinBinder.TryBindJoin` only sets `JoinScope` when the result selector captures a whole side. For the **already-fully-flattened** case (`(o, i) => new { o.Name, i.Total }`, `JoinScope` stays `null`), this new arm's guard is false, and the lookup instead needs to fall through to... check: today's `else` branch throws unconditionally for anything not matching the first three arms. A flattened-only join's lookup would hit this same new condition's `IsCollection: false && ForceUnwind` half but fail the `JoinScope is { }` half (since no scope was set) — it would then fall through to the existing collection-flatten arm's `else if` (also false, since `IsCollection: false`) and hit the final `else` throw. **Fix:** broaden the new arm's guard to also match on `query.IsJoinQuery` generally, not just on `JoinScope` being non-null, since the flattened-only case is equally eligible for the same lowering — only the *shape of what runs afterward* (whether later operators can resolve fields against a scope) differs, not whether the `$lookup`/`$unwind` themselves should be emitted:

```csharp
            else if (lookup.Navigation is { IsCollection: false } && lookup.ForceUnwind && query.IsJoinQuery)
            {
                var isLeftOuter = query.Select.JoinScope is { } joinScope && joinScope.InnerPrefix == lookup.As
                    ? joinScope.IsLeftOuter
                    : query.Joins.First(j => j.Alias == lookup.As).IsLeftOuter;
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: isLeftOuter));
            }
```

This reads `IsLeftOuter` from `MongoJoinScope` when a scope was recorded (whole-side capture), or directly from the matching `JoinInfo` on `query.Joins` otherwise (flattened-only case) — both are populated by the time lowering runs, and `query.Joins` (Task 3's consumed `JoinInfo` list) is already `IReadOnlyList<JoinInfo>` on `MongoQueryExpression` (`Expressions/MongoQueryExpression.Lookup.cs:169`), so no new state is needed for this fallback read.

- [ ] **Step 3: Run the Task 4 smoke test again**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: Still likely FAIL, but check the NEW failure mode — the `$project` following the `$unwind` must still resolve `o.Name`/`r.Total` to real field refs. If `NativeProjectionBinder`/`NativeSlotPopulator` don't yet know how to read a member off the inner-scope-prefixed lookup result for an already-flattened join projection, this is expected and is exactly the seam Task 6 must close (see Task 6 Step 1's investigation). Record the exact exception message here before proceeding, so Task 6 starts from a known, reproduced failure rather than a guess.

- [ ] **Step 4: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs
git commit -m "EF-392: lower a native Join/LeftJoin's registered lookup into \$lookup/\$unwind stages"
```

---

### Task 6 (REVISED 2026-08-26 — see below): Document dormant scaffolding and verify graceful fallback

**This task's original scope ("resolve join-scope member access in Where/OrderBy/Select") is abandoned.** Its implementer discovered, via real investigation (not guesswork), that EF Core's `NavigationExpandingExpressionVisitor` normalizes every `Join`/`LeftJoin` call — genuine and Include-generated alike — into the identical `TransparentIdentifier(Outer, Inner)` shape before `TranslateJoinCore` ever runs. The "flattened vs. whole-side-capture" distinction Tasks 2/3/5 were built on does not exist at that point; both shapes are indistinguishable there. This is an EF Core limitation. The human maintainer ruled: do not build a provider-side disambiguation mechanism (no candidate/confirm handshake) — genuine two-sided joins stay on the driver-LINQ fallback indefinitely. See `docs/superpowers/specs/2026-08-26-native-join-translation-design.md`'s "Blocker found during implementation" section and [EF-439](https://jira.mongodb.org/browse/EF-439) (the follow-up ticket) for full detail.

Tasks 1–5 remain committed as-is (Task 1 is a live, valuable refactor; Tasks 2/3/5 are now provably unreachable dead code, kept intentionally as dormant scaffolding per an explicit decision, not reverted). **This task's real, revised job:**

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs`, `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs`, the new arm in `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, and the `JoinScope` property on `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add a doc comment to each pointing at the spec's "Blocker found during implementation" section and EF-439, explaining the code is currently unreachable and why, so a future reader doesn't mistake it for live, exercised logic.
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — revise the existing smoke test(s): a genuine two-sided `Join`/`LeftJoin` must (a) succeed and return CORRECT results under the default `MongoQueryMode.Native` (via the driver-LINQ fallback — this already worked before this plan started and must keep working), and (b) throw `NativeTranslationNotSupportedException` cleanly under `MongoQueryMode.NativeOnly` (the expected, permanent decline — not a bug to fix). Do NOT assert success under `NativeOnly` — that was this task's original (now-abandoned) goal.

**Interfaces:** none new — this task only adds documentation comments and revises test expectations to match the ruled-on permanent behavior.

- [ ] **Step 1: Add the dormant-code doc comments**

To each of the four locations above, add an XML doc `<remarks>` block (or extend the existing one) stating: this member/type is currently unreachable — `MongoJoinBinder.TryBindJoin` declines every real join before this code can run, because EF Core's `NavigationExpandingExpressionVisitor` makes a genuine join and an Include-generated join indistinguishable at bind time. Point to `docs/superpowers/specs/2026-08-26-native-join-translation-design.md` and EF-439. Keep it short — one paragraph, not a re-derivation of the whole investigation.

- [ ] **Step 2: Revise the functional smoke test(s) in `NativeJoinTests.cs`**

Replace `Flattened_scalar_join_goes_native_with_correct_results_and_mql` (and drop the second, whole-side-capture smoke test from Task 4/6's original plan — it tested a shape that can never go native) with two tests:

```csharp
[Fact]
public void Genuine_two_sided_join_returns_correct_results_via_fallback()
{
    var seed = SeedOwnersAndOrders();
    using var db = CreateContextWithLogging(seed, MongoQueryMode.Native,
        nameof(Genuine_two_sided_join_returns_correct_results_via_fallback), out _);

    var result = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
        .AsEnumerable()
        .OrderBy(x => x.Name).ThenBy(x => x.Total)
        .ToList();

    var expected = seed.Owners
        .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
        .OrderBy(x => x.Name).ThenBy(x => x.Total)
        .ToList();

    Assert.Equal(expected, result);
}

[Fact]
public void Genuine_two_sided_join_declines_cleanly_under_NativeOnly()
{
    // Permanent, correct behavior per EF-439 — a genuine two-sided join can never be
    // distinguished from an Include-generated one at bind time (EF Core limitation), so
    // it must always decline to the driver-LINQ fallback, never attempt native translation.
    var seed = SeedOwnersAndOrders();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Genuine_two_sided_join_declines_cleanly_under_NativeOnly));

    Assert.Throws<NativeTranslationNotSupportedException>(() =>
        db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .ToList());
}
```

- [ ] **Step 3: Run both tests, confirm they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: PASS, both tests.

- [ ] **Step 4: Run the full functional Query regression suite**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`
Expected: PASS, no regressions (in particular `NativeSelectManyTests`, all reference-Include tests).

- [ ] **Step 5: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoJoinScope.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-392: document dormant join-scaffolding as blocked on EF-439; verify genuine two-sided joins fall back correctly"
```

---

### ORIGINAL Task 6 text (abandoned — kept for historical record only, DO NOT EXECUTE)

<details>
<summary>Original plan text, superseded by the revision above</summary>

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (add a join-scope-aware entry point alongside its existing bare/wrapped projection binding)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (add a join-scope-aware predicate/sort-key translation path, called from `PopulateNativeSlots` for `Where`/`OrderBy`/`ThenBy` when `mongoQ.Select.JoinScope != null`)
- Test: extend `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `MongoJoinScope` (Task 2), `MongoTransparentScopeResolver` (Task 1), `MongoExpressionTranslator`'s existing two-scope constructor (`MongoExpressionTranslator(IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string innerPrefix)` — `NativeTranslation/MongoExpressionTranslator.cs:72-79`, unchanged).
- Produces: `Where`/`OrderBy`/`Select` lambdas whose single parameter's member access resolves against `MongoSelectDefinition.JoinScope`'s `OuterMemberName`/`InnerMemberName` translate correctly; an already-flattened (no-`JoinScope`) join's `Select` continues through the existing single-scope `NativeProjectionBinder` path unchanged, since every member is already a plain scalar leaf resolvable with no scope tracking at all.

- [ ] **Step 1: Confirm the exact current failure from Task 5 Step 3 and design the fix around it**

Run the `NativeJoinTests` smoke test again and read the exact `NativeTranslationNotSupportedException` (or wrong-result) it now produces. For the specific smoke test written in Task 4 (`(o, r) => new { o.Name, r.Total }` — an ALREADY-FLATTENED result selector, no `JoinScope` set), the failure is most likely in `NativeSlotPopulator`/`NativeProjectionBinder`'s trailing-`Select` handling: after `TranslateJoinCore` substitutes `resultSelector.Body` directly (Task 4's unchanged `newResultSelector` line), the shaper carries a `NewExpression` whose arguments are `o.Name`/`r.Total` member expressions *already resolved against the outer/inner shapers* — but there is no SEPARATE trailing `Select` call for `NativeProjectionBinder` to bind against (unlike SelectMany's deferred-Select pattern), because a fluent `.Join(...)` with an inline result selector produces the projection as part of the join call itself, with no additional LINQ operator. **This means the flattened-join projection needs its own binder entry point, invoked directly from `TranslateJoinCore` (in `MongoJoinBinder.TryBindJoin`, Task 3) rather than from a later `TranslateSelect`.**

Revise Task 3's `MongoJoinBinder.TryBindJoin` (this task modifies it further) so that for each already-flattened member (`argExpr` is neither `outerParam` nor `innerParam` verbatim), it immediately translates the leaf via the two-scope `MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, joinInfo.Alias)` constructor and adds it to `outerQueryExpression.Select.Projection` directly — mirroring exactly how `NativeSelectManyBinder.TryBind`'s inner-Select form builds `MongoProjection`s inline (`NativeSelectManyBinder.cs:98-113`), rather than deferring to a later call.

- [ ] **Step 2: Update `MongoJoinBinder.TryBindJoin` to translate and register flattened leaves inline**

```csharp
// In MongoJoinBinder.cs, replace the member-classification loop from Task 3 with:

        var outerTranslator = new MongoExpressionTranslator(outerQueryExpression.CollectionExpression.EntityType);
        var twoScopeTranslator = new MongoExpressionTranslator(
            navigation.TargetEntityType, outerParam, outerQueryExpression.CollectionExpression.EntityType, joinInfo.Alias);

        string? outerMemberName = null;
        string? innerMemberName = null;
        var flattenedProjections = new System.Collections.Generic.List<MongoProjection>();

        foreach (var (alias, argExpr) in members)
        {
            if (ReferenceEquals(argExpr, outerParam))
            {
                if (outerMemberName != null) return false;
                outerMemberName = alias;
                continue;
            }

            if (ReferenceEquals(argExpr, innerParam))
            {
                if (innerMemberName != null) return false;
                innerMemberName = alias;
                continue;
            }

            // Already-flattened scalar leaf (e.g. o.Name or r.Total): translate now, against whichever
            // side it's rooted on, using the SAME two-scope translator SelectMany's correlated filters use
            // (parameter identity, never member name) so a member name shared between outer and inner
            // entity types can never mis-scope.
            if (!twoScopeTranslator.TryTranslate(argExpr, out var translated)
                && !outerTranslator.TryTranslateField(argExpr, out _)) // outer-rooted leaves also need a value, not just a field-existence check
            {
                return false;
            }

            if (!twoScopeTranslator.TryTranslateValue(argExpr, out var value))
            {
                return false;
            }

            flattenedProjections.Add(new MongoProjection(alias, value));
        }
```

**Note for the implementer:** the exact translator call (`TryTranslate` vs `TryTranslateValue` vs `TryTranslateField`) needs to be re-checked against `MongoExpressionTranslator`'s real public surface in `NativeTranslation/MongoExpressionTranslator.cs` before finalizing this step — the sketch above calls `TryTranslate` speculatively as a probe and then re-translates with `TryTranslateValue`, which is redundant and should be collapsed to a single call once confirmed. Read `MongoExpressionTranslator.cs` in full (only lines 1-150 were read while writing this plan) to settle on the one correct method for "translate a projection leaf value, two-scope" before writing this code for real, and simplify accordingly — do not ship the redundant double-call above as-is.

Then, at the end of the method (replacing Task 3's final block):

```csharp
        var lookup = new LookupExpression(navigation, forceUnwind: true)
        {
            PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
        };
        outerQueryExpression.AddLookup(lookup);

        foreach (var projection in flattenedProjections)
        {
            outerQueryExpression.Select.AddProjection(projection);
        }

        if (outerMemberName != null || innerMemberName != null)
        {
            outerQueryExpression.Select.JoinScope = new MongoJoinScope(
                outerMemberName, innerMemberName,
                outerQueryExpression.CollectionExpression.EntityType, navigation.TargetEntityType,
                joinInfo.Alias, joinInfo.IsLeftOuter);
        }

        return true;
```

- [ ] **Step 3: Run the smoke test again**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: PASS for the flattened-scalar smoke test. If it still fails, the failure is now isolated to how `NativeSlotPopulator`'s existing `else` catch-all (`Query/AGENTS.md`'s "native catch-all whitelist must stay in sync" pitfall) treats a `Select` whose `Route` was already set by this direct registration — check whether `TranslateSelect`'s own dispatch needs a guard added (mirroring `IsTransparentIdentifierSelector`/`IsSingleLevelCollectionIncludeSelector`) so it does not re-decide a projection this binder already populated. This is the single highest-uncertainty step in this plan — budget real investigation time here, not a quick guess.

- [ ] **Step 4: Add a second functional test for the whole-side-capture (`JoinScope`-populated) case, with a subsequent `Where`**

```csharp
    [Fact]
    public void Whole_side_capture_with_trailing_Where_goes_native_with_correct_results_and_mql()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_side_capture_with_trailing_Where_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.r.Total > 100)
            .Select(x => new { x.o.Name, x.r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.r.Total > 100)
            .Select(x => new { x.o.Name, x.r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"$lookup\"", message);
        Assert.Contains("\"$unwind\"", message);
    }
```

This exercises `NativeSlotPopulator`'s `Where` translation reading `x.r.Total` through `MongoJoinScope` — implement the resolution path in `NativeSlotPopulator.PopulateNativeSlots`'s `Where` arm: when `mongoQ.Select.JoinScope is { } scope`, re-root the predicate the same way `NativeSelectManyBinder.TryBindTransparentIdentifierProjection` re-roots (`MongoTransparentScopeResolver`, hop names `[scope.OuterMemberName!, scope.InnerMemberName!]` fed as a **single-hop-each** lookup — since a Join's scope, unlike SelectMany's nested nesting, is always exactly one flat member access `x.o`/`x.r`, not a chain — so this reuses the resolver's `sourceCount: 1` case directly: `x.<OuterMemberName>` matches hop name `OuterMemberName` at scope 0 is WRONG for this shape (the resolver's hop-name convention is positional — `hopNames[0]` is the "step toward root" name and `hopNames[1]` is the "step to this scope" name, which doesn't fit a Join's two SIBLING member names naturally). **Do not force-fit `MongoTransparentScopeResolver`'s exact algorithm here** — for Join's flat (non-nested) two-member shape, write a simpler direct check instead: a member access `MemberExpression { Expression: ParameterExpression p, Member.Name: var name }` where `p` is the current lambda's own parameter resolves to the outer scope when `name == scope.OuterMemberName`, the inner scope when `name == scope.InnerMemberName`, translating the REST of the access chain (everything after peeling that one hop) against the matching entity type via the existing two-scope `MongoExpressionTranslator` constructor exactly as Task 6 Step 2 already does for the flattened case. Implement this as a small dedicated helper in `NativeSlotPopulator.cs` (or `MongoJoinBinder.cs`, since it's join-specific, not a generalization of the SelectMany walker) — e.g. `MongoJoinBinder.TryTranslateScopedPredicate(MongoJoinScope scope, ParameterExpression param, Expression body, out MongoExpression? result)` — rather than routing through `MongoTransparentScopeResolver`, since that resolver's nested-chain algorithm does not match this flat shape. Revise Task 1's docstring/remarks if this narrows its intended reuse — record that deviation in the PR description when this task lands.

- [ ] **Step 5: Run all `NativeJoinTests`, then the full functional Query suite for regressions**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: PASS, both tests.

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`
Expected: PASS, no regressions (in particular `NativeSelectManyTests`, all reference-Include tests, `QueryModeGateTests`/`QueryModeGateIncludeTests`).

- [ ] **Step 6: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoJoinBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-392: resolve join-scope member access in Where/Select; close out flattened and whole-side-capture Join shapes"
```

</details>

---

### Task 7 (REVISED framing, same content): Declining regression tests for explicit non-goals

**Framing update:** these shapes now decline for the SAME underlying reason as every genuine two-sided join (the Task 4 `TransparentIdentifier` guard, now understood to be permanent per EF-439), not for their originally-assumed distinct reasons (GroupJoin's own array shape, chained-join detection, etc. — those guards may still exist and may also independently decline, but the guard that fires first today is the blanket one). The tests below are still valid and worth keeping: they pin that these shapes decline *cleanly* (a `NativeTranslationNotSupportedException`, not a crash) and that results stay correct via fallback — that guarantee doesn't change, only the internal reason does.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:** none new — test-only task.

- [ ] **Step 1: Add a declining test for GroupJoin's own array/grouped result shape**

```csharp
    [Fact]
    public void GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly()
    {
        // Owned by EF-436, not this ticket — must stay declining, not silently "fixed" by this work.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .GroupJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o.Name, Orders = rs })
                .AsEnumerable()
                .ToList());
    }
```

- [ ] **Step 2: Add a declining test for a chained (second) join**

```csharp
    [Fact]
    public void Chained_second_join_still_declines_cleanly_in_NativeOnly()
    {
        var seed = SeedOwnersOrdersAndLines(); // extend the seed helper with a third related set
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_second_join_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(db.OrderLines, x => x.r.Id, l => l.OrderId, (x, l) => new { x.o.Name, l.Sku })
                .AsEnumerable()
                .ToList());
    }
```

- [ ] **Step 3: Add a declining test for a query-filtered join target**

```csharp
    [Fact]
    public void Query_filtered_join_target_still_declines_cleanly_in_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Query_filtered_join_target_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders.Where(r => r.Total > 0), o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }
```

- [ ] **Step 4: Add a declining test for a navigation-less key-equality join**

```csharp
    [Fact]
    public void Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly()
    {
        // A Join on two properties with no corresponding model navigation between the two entity types.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Region, r => r.Region, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }
```

(Requires the seed fixture's `Owner`/`Order` types to share a non-FK `Region` property with no declared navigation between them — add this to the shared fixture helper if it doesn't already exist, following whatever minimal-entity pattern `SeedOwnersAndOrders` already established in Task 4.)

- [ ] **Step 5: Run all four new tests and confirm each throws for the expected reason (not a coincidental unrelated failure)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoinTests"`
Expected: PASS — all four `Assert.Throws` succeed. If any instead throws before reaching the exercised code path (e.g. a fixture-setup exception), fix the fixture, not the assertion.

- [ ] **Step 6: Build all three EF configs and commit**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-392: pin explicit Join non-goals (GroupJoin array shape, chained joins, filtered target, navigation-less join) as declining"
```

---

### Task 8 (REVISED framing 2026-08-26): Differential-correctness oracle fixture and full regression pass

**Framing update:** per EF-439, genuine two-sided joins now permanently execute via the driver-LINQ fallback, not natively. The oracle theory below is still valuable — it proves matched/unmatched/dangling-FK correctness end-to-end — but drop `MongoQueryMode.NativeOnly` from its context (that mode would now always throw for these shapes, by design) and use the default `MongoQueryMode.Native` instead, which exercises the fallback. Rename the test to drop the word "native" so it doesn't misdescribe what it's proving.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:** none new — test-only task.

- [ ] **Step 1: Read `NativeOwnedCollectionAllTests.cs`'s oracle-`[Theory]` pattern before writing this**

Per the spec's own reference, read `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs` for the exact established shape: a `[Theory]` feeding a fixture deliberately covering matched/unmatched/edge states, asserting the native result equals an in-memory LINQ oracle evaluated over the *same* `Expression` object. Mirror that structure exactly rather than inventing a new one.

- [ ] **Step 2: Write the Join oracle theory**

```csharp
    public static IEnumerable<object[]> JoinOracleCases()
    {
        // Each case is a Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<...>> so the SAME expression
        // tree runs against both the native-backed DbSet and the in-memory seed collection, following the
        // NativeOwnedCollectionAllTests.cs oracle pattern exactly.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total }))
        ];
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.LeftJoin(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, Total = (decimal?)r.Total }))
        ];
    }

    [Theory]
    [MemberData(nameof(JoinOracleCases))]
    public void Join_result_matches_in_memory_oracle_including_unmatched_rows(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>> query)
    {
        // Seed must include: an Owner with a matching Order, an Owner with NO Order (left-join/unmatched
        // case), and an Order whose OwnerId matches nothing (dangling FK, dropped by inner Join).
        // Runs under the default MongoQueryMode.Native — genuine two-sided joins execute via the driver-LINQ
        // fallback (see EF-439), not natively; this proves that fallback path is correct, not new native
        // coverage. Do NOT use MongoQueryMode.NativeOnly here — it would always throw for this shape, by design.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows));

        var actualResult = query(db.Owners, db.Orders).AsEnumerable().ToList();
        var oracleResult = query(seed.Owners.AsQueryable(), seed.Orders.AsQueryable()).ToList();

        Assert.Equal(oracleResult, actualResult);
    }
```

- [ ] **Step 3: Run the theory and fix any real mismatch it surfaces**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Join_result_matches_in_memory_oracle"`
Expected: PASS for both cases (this exercises the pre-existing driver-LINQ fallback, which already worked before this plan — a failure here would be a genuine, unrelated regression, not something this plan's own code should be able to cause, since none of Tasks 1-6's dormant/live code paths are reachable for this shape).

- [ ] **Step 4: Run the complete regression surface — Query unit tests, Query functional tests, and the full spec suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
```

Expected: all PASS. Per EF-439, do NOT expect any NEW passes in the `MONGODB_EF_NATIVE_ONLY=1` run attributable to Join/LeftJoin shapes — that would indicate either a leftover expectation mismatch in this revised plan or (more concerning) that the "indistinguishable at bind time" finding was wrong; investigate rather than assume it's progress. Any genuinely NEW failure anywhere in this run is still a regression to investigate before proceeding.

- [ ] **Step 5: Invoke the `/test-all` skill (or run all three EF configs manually) for full multi-version confirmation**

Run: `.claude/skills/test-all/` (via the `/test-all` slash command) or manually:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

Expected: all three EF versions green.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "EF-392: add differential-correctness oracle theory for Join/LeftJoin fallback (matched/unmatched/dangling-FK rows); ref EF-439"
```

---

## Self-Review

**Spec coverage:**
- Two-scope resolver extraction as an early, isolated commit → Task 1. ✓
- `MongoJoinBinder` invoked from `TranslateJoinCore` only for a genuine two-sided join (Include sugar handled separately by the unchanged `TryConfirmReferenceInclude`) → Tasks 3-4. ✓
- Join-scope marker on `MongoSelectDefinition` extending `HasTerminalOperator` → Task 2. ✓
- New `AppendLookupStages` branch reusing `MongoLookupStage`/`MongoUnwindStage` keyed on `isLeftOuter` → Task 5. ✓
- Explicit non-goals get their own declining regression tests → Task 7 (GroupJoin array shape, chained joins, query-filtered target, navigation-less join — the fourth was surfaced during grounding as a real additional boundary the spec's "widened `$lookup` eligibility" language implied but didn't enumerate by name; included for completeness). ✓
- Testing per the spec (`NativeJoinTests`, differential-correctness oracle, unit tests, SelectMany/reference-Include regression) → Tasks 1 (unit), 3 (unit), 4/6/7 (functional), 8 (oracle + full regression). ✓
- EF-411 coordination (small early commit, check before merging `MongoSelectDefinition.cs`) → Task 2's coordination note. ✓

**Placeholder scan:** No "TBD"/"TODO"/"implement later" found. Two spots are flagged as genuine open design decisions rather than placeholders — Task 6 Step 1's revision of `MongoJoinBinder` (the flattened-leaf translation call needs a implementer to re-verify against the real `MongoExpressionTranslator` surface beyond what was read while writing this plan) and Task 6 Step 4 (explicitly rejects force-fitting `MongoTransparentScopeResolver` and specifies the concrete alternative to write instead). These are marked as the plan's own highest-uncertainty points with a stated fallback approach, not left blank.

**Type consistency:** `MongoJoinScope`'s constructor/property names (`OuterMemberName`, `InnerMemberName`, `OuterEntityType`, `InnerEntityType`, `InnerPrefix`, `IsLeftOuter`) are used identically across Tasks 2, 3, 5, and 6. `MongoJoinBinder.TryBindJoin`'s signature is introduced in Task 3 and its call site in Task 4 matches exactly (`outerQueryExpression, innerQueryExpression, resultSelector, joinInfo`); Task 6 revises the method's *body* but not its signature. `MongoTransparentScopeResolver.TryResolveScopeDepth`/`ScopeRerootingVisitor` from Task 1 are referenced by `NativeSelectManyBinder`'s updated call sites in Task 1 Step 5, and explicitly declined for reuse in Task 6 Step 4 (with the reasoning recorded) rather than silently diverging.

**Deviation from the spec worth flagging to the reviewer:** the spec's Task 1 framing ("extract a shared two-scope parameter-identity resolver out of NativeSelectManyBinder") turned out, on reading the real code, to already partly exist as `MongoExpressionTranslator`'s two-scope constructor (reused as-is, no extraction needed) — the genuine extraction target is the narrower *scope-depth/re-rooting walker* (`TryResolveScopeDepth`/`ScopeRerootingVisitor`), which Task 1 does extract and generalize. That extraction remains live and valuable regardless of everything below.

**MAJOR REVISION (2026-08-26, post-Task-5):** Task 6's original plan (a bespoke direct-check resolver for Join's flat two-sibling shape, explicitly avoiding `MongoTransparentScopeResolver`) was never executed. Its implementer instead discovered that the premise underlying Tasks 2/3/5/6 — that a genuine two-sided join is distinguishable from an Include-generated join at `TranslateJoinCore` — is false: EF Core's `NavigationExpandingExpressionVisitor` normalizes both into the identical `TransparentIdentifier(Outer, Inner)` shape before either binder runs. (Ironically, had this premise held, `MongoTransparentScopeResolver` at `sourceCount: 1` *would* have been the right tool after all — the "don't force-fit it" instruction was itself downstream of the same false premise.) The human maintainer ruled: do not build a disambiguation workaround; genuine two-sided joins stay on the pre-existing, correct driver-LINQ fallback indefinitely. Filed as [EF-439](https://jira.mongodb.org/browse/EF-439). Task 6 was revised in place to document Tasks 2/3/5 as intentionally-retained dormant scaffolding and to correct the smoke tests' expectations; Tasks 7-8's tests remain valid but their framing was corrected to stop implying native execution. See the design spec's "Blocker found during implementation" section for the full account. This is exactly the kind of finding `writing-plans`/`subagent-driven-development`'s process is built to surface — a plan built on a wrong assumption about the platform it targets — not a failure of execution at any individual task.
