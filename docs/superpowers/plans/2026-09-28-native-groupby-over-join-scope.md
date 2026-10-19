# Native GroupBy over a join scope (join-then-group) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `GroupBy` composed on a native join scope (a reference navigation, an explicit `Join`, a
`GroupJoin`/`DefaultIfEmpty` left join, a self-join, or a two-level chain) translate natively instead of
falling back to driver-LINQ.

**Architecture:** `NativeGroupByBinder` today translates every key part, accumulator operand, and element
predicate with a root-entity-only `MongoExpressionTranslator`, so a body over the join's
`TransparentIdentifier(Outer, Inner)` element fails and the query falls back. We introduce a small
`MongoGroupElementTranslator` that routes a body referencing the transparent-identifier element parameter
through the existing `NativeJoinScopeTranslator.TryTranslateSingleScope` (which resolves each `Outer`/`Inner` hop
chain to one scope), and everything else to the root translator. `TranslateGroupBy` decides once, with no
mutation, whether the join scope is safe to group over (`MongoSelectDefinition.GroupByJoinScope`); the binder
confirms the join chain (`NativeJoinScopeProjectionBinder.ConfirmEntireChain`) only at its commit point, after
every gate passed. The lowerer already emits `$lookup`/`$unwind` → post-join ops → `$group`, so no lowering work
is needed.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (build configurations `Debug EF8|EF9|EF10`), MongoDB C# driver,
xUnit (plain `Assert.*`), Docker/TestContainers for functional + specification tests.

**Spec:** No separate spec doc; the design comes from the 2026-09-28 investigation, recorded in the "Background"
section below and in the parent design
`docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` ("Scope" section — the 6 deferred
tests).

## Background (the investigation this plan argues from)

Measured at `fdd36a39` on EF10 with `MONGODB_EF_NATIVE_ONLY=1`: 6 `NorthwindGroupByQueryMongoTest` methods pass
only via driver-LINQ because they group over a join:
`GroupBy_required_navigation_member_Aggregate`, `GroupBy_with_group_key_access_thru_navigation`,
`Join_GroupBy_Aggregate`, `Self_join_GroupBy_Aggregate` (single-level), and
`GroupBy_multi_navigation_members_Aggregate`, `Join_groupby_anonymous_orderby_anonymous_projection` (composite
key spanning scopes / depth-2 chain / 1:N collection-nav join).

They decline at three shared layers:
1. `TryBindGroupKey` (`NativeGroupByBinder.cs:49`) translates with a root-only translator → the key over
   `ti.Outer.X` / `ti.Inner.Y` fails → `MarkNotNativelyRepresentable` at
   `MongoQueryableMethodTranslatingExpressionVisitor.cs:1893`.
2. With no `PendingGroupKey`, `TryBindGroupProjection` fails (QMTEV:324); accumulator operands would fail the same
   way (`NativeGroupByBinder.cs:624, 724, 924, 1004`).
3. Nothing confirms the join: `TranslateJoinCore` records a candidate, and `HasUnconfirmedCandidateJoin`
   (`MongoSelectDefinition.cs:~917`) forces `Route` to `Fallback`. The grouped `Select` branch (QMTEV:317-337)
   has no confirm step, and `IsSingleEligibleNativeJoinScope` (QMTEV:722) can't be reused because it rejects
   `HasTerminalOperator` (true once `IsGroupBy` is set) and, on success, **mutates** (it calls
   `DeferPipelineOpsPastConfirmedJoin`).

A throwaway ~40-line spike wiring (1)–(3) made all 6 return correct data under NativeOnly, and additionally 8
tests that today assert a translation failure started returning correct data natively
(`GroupBy_optional_navigation_member_Aggregate`, `GroupBy_principal_key_property_optimization`,
`GroupBy_with_group_key_access_thru_nested_navigation`, `GroupJoin_GroupBy_Aggregate` 1–5). The spike skipped
the paging guards; this plan does not.

**Deliberate design decisions (do not relitigate during implementation):**
- **Paging recorded before a GroupBy over a join that is not 1:1 DECLINES** (no deferral). Deferring paging
  past the `$lookup` (what `IsSingleEligibleNativeJoinScope` does for projections) is an accepted consequence
  there because it changes rows only for dangling references; for a GroupBy it changes *group counts* silently,
  so it is not acceptable here. Paging over an all-1:1 join (every join a left-outer reference navigation —
  `MongoQueryExpression.AreAllJoinsRowCountPreserving()`) stays ahead of the `$lookup` and is correct.
- **Chains (`Levels.Count > 1`) with any recorded paging decline** (the chain-paging snapshot gap documented at
  QMTEV:484-487).
- **Join mode only when the GroupBy key selector's parameter is a `TransparentIdentifier`**. A join whose
  result selector already projected one side (`(o, x) => o` or `(o, x) => x`) hands GroupBy an entity-typed
  parameter; the root translator would resolve `x.Country` by member name against the *outer* entity type and
  silently read the wrong field. Those shapes keep falling back exactly as today.
- **In join mode, a body referencing any parameter other than the single transparent-identifier element
  parameter declines.** Parameter-free bodies (constants, captured parameters) use the root translator.
- **Distinct accumulators (`TryTranslateField`) over the join element decline** in join mode —
  `NativeJoinScopeTranslator` has no field mode. Out of scope.
- **`PriorGrouping` (a GroupBy over a finalized grouping) and join mode are mutually exclusive**; join mode is
  enabled only when there is no finalized prior grouping.
- **Confirm-then-decline is accepted**, as it already is for the existing join `Select` arms: once the grouped
  `Select` confirms the join, the `$lookup` is registered at translation time, which flips the driver-LINQ
  fallback to the flat `_lookup_<Nav>` document shape if a *later* operator declines. The spike measured that
  fallback returning correct data (`GroupBy_Min_Where_optional_relationship(_2)`); Task 2 pins it with a
  differential test.
- Group-then-join (`IsGroupByFallbackUnsafe`, QMTEV:1936-1941) and the CSHARP-6017 inner-paging hard decline
  (join inners that aren't bare scans never get a `JoinScope`) are **untouched**.

## Global Constraints

- Work directly on branch `native-EF-322-Native-LINQ-rebased` (no worktree). Commit messages start with
  `EF-322: `. Do NOT push; the owner decides pushing/squashing.
- Preserve file BOMs. `src/` is nullable-enabled — annotate new types.
- `<NoWarn>EF1001</NoWarn>` already covers EF internal API use; no new suppressions.
- Multi-version: code must compile and pass under `Debug EF8`, `Debug EF9`, `Debug EF10`. Use `#if` only if a
  version genuinely differs (none expected).
- Tests: xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset (TestContainers). Never enable test
  parallelization.
- A recognizer must not mutate then decline: stage into locals, commit only after every gate passes.
- `Native == DriverLinq == hand-computed oracle` for every new native shape; new native join shapes MUST also be
  run under explicit `MongoQueryMode.DriverLinq` (`NativeModeAssert.NativeAndParity` does this).
- Per-task scratch logs go under a unique directory:
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/task<N>/`.

## Review Focus

1. **Left join with no match** (optional nav null, dangling optional FK, `DefaultIfEmpty` with no inner row):
   the row must still be counted (`Count()` includes it) and grouped under a `null` key when the key reads the
   inner side. Pinned by Task 2 `Optional_navigation_key_groups_unmatched_rows_under_a_null_key`.
2. **Required reference with a dangling FK** must be dropped before grouping (inner join), and paging written
   *before* the navigation must not be silently re-applied to the joined rows. Pinned by Task 2
   `Required_navigation_key_drops_dangling_rows` and `Paging_before_a_required_navigation_group_declines_cleanly`.
3. **An accumulator over an inner-side value that is null/missing** (`Max(o => o.Owner.Rank)` where `Rank` is
   null for a whole group) must yield `null`, not `0` or a throw. Pinned by Task 2
   `Inner_side_accumulator_over_all_null_values_is_null`.
4. **A join whose result selector projected only one side** (`(o, x) => x`) must NOT go native via name-based
   resolution against the outer entity. Pinned by Task 2
   `GroupBy_over_a_join_projecting_only_the_inner_side_declines_cleanly` and Task 1's translator unit tests.
5. **A later operator that declines after the grouped Select confirmed the join** must still return correct data
   through the fallback. Pinned by Task 2 `Post_group_where_after_a_confirmed_join_group_declines_cleanly`.

---

### Task 1: `MongoGroupElementTranslator` and threading it through `NativeGroupByBinder` (behavior-neutral)

This task introduces the routing translator and makes the binder use it everywhere, but always constructs it
with `joinScope: null`, so behavior is unchanged. Task 2 turns join mode on.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoGroupElementTranslator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` (every
  `MongoExpressionTranslator translator` parameter and the four `new MongoExpressionTranslator(...)` sites at
  ~:49, ~:192, ~:1069, ~:1149)
- Create: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoGroupElementTranslatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class MongoGroupElementTranslator
  {
      internal MongoGroupElementTranslator(IEntityType rootEntityType, MongoJoinScope? joinScope, MongoGrouping? priorGrouping);
      internal bool IsJoinScoped { get; }
      internal bool TryTranslateValue(Expression body, [NotNullWhen(true)] out MongoExpression? result);
      internal bool TryTranslate(Expression body, [NotNullWhen(true)] out MongoExpression? result);
      internal bool TryTranslateField(Expression body, [NotNullWhen(true)] out MongoFieldExpression? result);
  }
  ```
  and in `NativeGroupByBinder`: `private static MongoGroupElementTranslator CreateElementTranslator(MongoQueryExpression mongoQ)`.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoGroupElementTranslatorTests.cs`
(copy the license header from `NativeJoinScopeTranslatorTests.cs`; save with a UTF-8 BOM like its neighbours):

```csharp
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

// Same metadata pattern as NativeJoinScopeTranslatorTests: real IEntityType metadata and a real
// TransparentIdentifierFactory type (its Outer/Inner are fields, so use Expression.PropertyOrField).
public class MongoGroupElementTranslatorTests
{
    private class OuterEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class InnerEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Total { get; set; }
    }

    private const string InnerPrefix = "_lookup_Inner";

    private static (IEntityType Outer, IEntityType Inner) GetEntityTypes()
    {
        using var db = SingleEntityDbContext.Create<OuterEntity>(mb => mb.Entity<InnerEntity>());
        return (db.Model.FindEntityType(typeof(OuterEntity))!, db.Model.FindEntityType(typeof(InnerEntity))!);
    }

    private static MongoGroupElementTranslator NewJoinScoped()
    {
        var (outer, inner) = GetEntityTypes();
        var scope = new MongoJoinScope(outer, [new MongoJoinScopeLevel(inner, InnerPrefix, isLeftOuter: false)]);
        return new MongoGroupElementTranslator(outer, scope, priorGrouping: null);
    }

    private static ParameterExpression NewElementParam()
        => Expression.Parameter(TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity)), "ti");

    [Fact]
    public void Root_mode_translates_a_root_entity_member()
    {
        var (outer, _) = GetEntityTypes();
        var translator = new MongoGroupElementTranslator(outer, joinScope: null, priorGrouping: null);
        var e = Expression.Parameter(typeof(OuterEntity), "e");

        Assert.False(translator.IsJoinScoped);
        Assert.True(translator.TryTranslateValue(Expression.Property(e, nameof(OuterEntity.Name)), out var result));
        Assert.Equal("Name", Assert.IsAssignableFrom<MongoFieldExpression>(result).ElementName);
    }

    [Fact]
    public void Join_mode_translates_an_outer_hop_unprefixed()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Name");

        Assert.True(translator.IsJoinScoped);
        Assert.True(translator.TryTranslateValue(body, out var result));
        Assert.Equal("Name", Assert.IsAssignableFrom<MongoFieldExpression>(result).ElementName);
    }

    [Fact]
    public void Join_mode_translates_an_inner_hop_under_the_lookup_prefix()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Inner"), "Total");

        Assert.True(translator.TryTranslateValue(body, out var result));
        Assert.Contains(InnerPrefix, Assert.IsAssignableFrom<MongoFieldExpression>(result).ToString());
    }

    [Fact]
    public void Join_mode_declines_a_body_spanning_both_scopes()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.Add(
            Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Id"),
            Expression.PropertyOrField(Expression.PropertyOrField(ti, "Inner"), "Total"));

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Join_mode_declines_an_entity_typed_parameter()
    {
        // Review Focus #4: a join whose result selector projected one side hands the binder an entity-typed
        // parameter. Name-based resolution against the root entity would silently read the wrong field.
        var translator = NewJoinScoped();
        var inner = Expression.Parameter(typeof(InnerEntity), "x");

        Assert.False(translator.TryTranslateValue(Expression.Property(inner, nameof(InnerEntity.Name)), out _));
        Assert.False(translator.TryTranslate(
            Expression.Equal(Expression.Property(inner, nameof(InnerEntity.Name)), Expression.Constant("a")), out _));
    }

    [Fact]
    public void Join_mode_translates_a_parameter_free_body_with_the_root_translator()
    {
        var translator = NewJoinScoped();

        Assert.True(translator.TryTranslateValue(Expression.Constant(5), out var result));
        Assert.IsType<MongoConstantExpression>(result);
    }

    [Fact]
    public void Join_mode_declines_TryTranslateField_over_the_element()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Name");

        Assert.False(translator.TryTranslateField(body, out _));
    }
}
```

Before relying on them, check the exact type names: `MongoFieldExpression`'s element-name property (read
`src/.../Query/Expressions/MongoFieldExpression.cs`), the constant node type (grep
`class Mongo.*ConstantExpression` under `Query/Expressions`), and how an inner-prefixed field exposes its prefix.
Adjust the assertions to the real members. Keep what each test asserts; only the member names may change. Mirror
the assertions `NativeJoinScopeTranslatorTests` uses for the same outer/inner cases.

- [ ] **Step 2: Run the tests and confirm they fail to compile**

Run: `dotnet build tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" 2>&1 | grep -E "error" | head`
Expected: `CS0246: The type or namespace name 'MongoGroupElementTranslator' could not be found`.

- [ ] **Step 3: Implement `MongoGroupElementTranslator`**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoGroupElementTranslator.cs` (license header,
UTF-8 BOM, same as neighbours):

```csharp
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates a GroupBy key part, accumulator operand, or element predicate for <see cref="NativeGroupByBinder"/>.
/// Over a plain collection this is the root-entity <see cref="MongoExpressionTranslator"/>. Over a native join
/// scope (<see cref="MongoSelectDefinition.GroupByJoinScope"/>) a body over the join's
/// <c>TransparentIdentifier(Outer, Inner)</c> element resolves each hop chain to one scope via
/// <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>.
/// </summary>
/// <remarks>
/// In join mode a body may reference only the single transparent-identifier element parameter. Any other
/// parameter declines: the root translator resolves members by name against the root entity, so an
/// entity-typed parameter from a join whose result selector already projected one side would silently read the
/// wrong field. Parameter-free bodies (constants, captured parameters) use the root translator.
/// </remarks>
internal sealed class MongoGroupElementTranslator
{
    private readonly MongoExpressionTranslator _rootTranslator;
    private readonly MongoJoinScope? _joinScope;

    internal MongoGroupElementTranslator(IEntityType rootEntityType, MongoJoinScope? joinScope, MongoGrouping? priorGrouping)
    {
        _rootTranslator = new MongoExpressionTranslator(rootEntityType);

        // A GroupBy over a prior finalized grouping resolves against that stage's flattened alias, never the
        // entity (see MongoExpressionTranslator.DistinctAliasScope). TranslateGroupBy never enables join mode then.
        if (priorGrouping != null)
            _rootTranslator.DistinctAliasScope = priorGrouping;

        _joinScope = priorGrouping == null ? joinScope : null;
    }

    internal bool IsJoinScoped => _joinScope != null;

    internal bool TryTranslateValue(Expression body, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(body, valueMode: true, out result);

    internal bool TryTranslate(Expression body, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(body, valueMode: false, out result);

    internal bool TryTranslateField(Expression body, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;

        // NativeJoinScopeTranslator has no field mode, so a distinct accumulator over the join element declines.
        if (_joinScope != null && FreeParameters.Of(body).Count > 0)
            return false;

        return _rootTranslator.TryTranslateField(body, out result);
    }

    private bool TryTranslateCore(Expression body, bool valueMode, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (_joinScope == null)
        {
            return valueMode
                ? _rootTranslator.TryTranslateValue(body, out result)
                : _rootTranslator.TryTranslate(body, out result);
        }

        var parameters = FreeParameters.Of(body);
        if (parameters.Count == 0)
        {
            return valueMode
                ? _rootTranslator.TryTranslateValue(body, out result)
                : _rootTranslator.TryTranslate(body, out result);
        }

        if (parameters.Count != 1 || !parameters[0].Type.IsTransparentIdentifierType())
            return false;

        return NativeJoinScopeTranslator.TryTranslateSingleScope(_joinScope, parameters[0], body, valueMode, out result);
    }

    // Parameters referenced by `body` but not declared by a lambda inside it.
    private sealed class FreeParameters : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _found = [];
        private readonly HashSet<ParameterExpression> _declared = [];

        internal static IReadOnlyList<ParameterExpression> Of(Expression body)
        {
            var visitor = new FreeParameters();
            visitor.Visit(body);
            return visitor._found;
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            foreach (var p in node.Parameters)
                _declared.Add(p);
            return base.VisitLambda(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_declared.Contains(node) && !_found.Contains(node))
                _found.Add(node);
            return node;
        }
    }
}
```

`IsTransparentIdentifierType` is the extension in `src/MongoDB.EntityFrameworkCore/ExpressionExtensionMethods.cs`
(namespace `MongoDB.EntityFrameworkCore`); add the `using` if needed. `MongoSelectDefinition.GroupByJoinScope`
does not exist until Task 2, so for now the `<see cref>` in the summary must be plain text
(`<c>MongoSelectDefinition.GroupByJoinScope</c>`), or the build fails with a cref warning-as-error; Task 2 turns it
into a `<see cref>`.

- [ ] **Step 4: Run the unit tests**

Run: `dotnet build tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" 2>&1 | tail -2 && dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoGroupElementTranslatorTests"`
Expected: 7 passed.

- [ ] **Step 5: Thread the translator through `NativeGroupByBinder`**

In `NativeGroupByBinder.cs`:

1. Add the factory (behavior-neutral for now: `joinScope: null`):
   ```csharp
   // Task 2 passes select.GroupByJoinScope here.
   private static MongoGroupElementTranslator CreateElementTranslator(MongoQueryExpression mongoQ)
       => new(mongoQ.CollectionExpression.EntityType, joinScope: null, mongoQ.Select.PriorGrouping);
   ```
2. Replace the translator construction plus the `DistinctAliasScope` assignment in `TryBindGroupKey` (~:49-54)
   and `TryBindGroupProjection` (~:192-196) with `var translator = CreateElementTranslator(mongoQ);`. The
   `PriorGrouping` handling now lives in the constructor.
3. Replace `new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType)` in
   `TryBindGroupTerminalAggregate` (~:1069) and `TryBindGroupWherePredicate` (~:1149) with
   `CreateElementTranslator(mongoQ)`. Neither site sets `DistinctAliasScope` today, so first check whether
   `PriorGrouping` can be non-null when they run. If it can, passing it would change behavior: keep those two
   sites behavior-identical by constructing
   `new MongoGroupElementTranslator(mongoQ.CollectionExpression.EntityType, joinScope: null, priorGrouping: null)`,
   and leave a one-line comment saying why.
4. Change every `MongoExpressionTranslator translator` parameter in the file to
   `MongoGroupElementTranslator translator` (about 12 methods: `TryBindKeyPartValue`,
   `TryBindNestedGroupProjectionConstruction`, `TryTranslateGroupProjectionExpression`,
   `TryTranslateGroupProjectionConditionOrValue`, `TryBindAccumulator`, `TryTranslateAccumulatorCondition`,
   `TryResolveOptionalAccumulatorSourceCondition`, `TryBindDistinctAccumulator`,
   `TryBindElementSelectedAccumulator`, `TryBindFilteredAccumulator`, `TryBindGroupPredicateComparison`,
   `TryBindGroupSideOperand`). The call sites (`translator.TryTranslateValue(...)`, `.TryTranslate(...)`,
   `.TryTranslateField(...)`) stay the same.
5. Grep for other callers of the changed private methods outside this file:
   `grep -rn "NativeGroupByBinder\." src | grep -v "NativeGroupByBinder.cs"`. If an `internal` method with a
   translator parameter is called elsewhere, construct a `MongoGroupElementTranslator` at that caller the same
   way.
6. Update the `<see cref="MongoExpressionTranslator.TryTranslateValue"/>` in `TryBindKeyPartValue`'s XML doc to
   point at `MongoGroupElementTranslator.TryTranslateValue`.

- [ ] **Step 6: Build all three configurations**

Run: `for c in EF8 EF9 EF10; do dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" 2>&1 | grep -E "Warn|Error\(s\)|error" | tail -3; done`
Expected: `0 Error(s)` for each, and no new warnings.

- [ ] **Step 7: Prove the refactor is behavior-neutral**

Run (EF10) and save the TRX under `.../scratchpad/task1/`:
```bash
S=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/task1; mkdir -p $S
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build 2>&1 | tail -1
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy|FullyQualifiedName~Join" 2>&1 | tail -1
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" --logger "trx;LogFileName=$S/nativeonly.trx" 2>&1 | tail -1
```
Expected: unit, functional and spec runs have 0 failures. The NativeOnly spec run shows exactly **16 failed /
493 passed / 4 skipped** (the pre-change measurement), and the failures are the same 8 methods × 2 listed in
"Background". Any difference means the refactor is not neutral: find out why before continuing.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoGroupElementTranslator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoGroupElementTranslatorTests.cs
git commit -m "EF-322: route NativeGroupByBinder translation through MongoGroupElementTranslator (no behavior change)"
```

---

### Task 2: Enable join mode: eligibility in `TranslateGroupBy`, confirm at the binder's commit point

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (new
  `GroupByJoinScope` property next to `JoinScope`, ~:719)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  (`TranslateGroupBy` ~:1842; a new `TryGetGroupByJoinScope` helper next to `IsSingleEligibleNativeJoinScope`
  ~:722)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`
  (`CreateElementTranslator`; commit points of `TryBindGroupProjection` ~:287-297 and
  `TryBindGroupTerminalAggregate`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoGroupElementTranslator.cs` (turn the
  plain-text reference into `<see cref="MongoSelectDefinition.GroupByJoinScope"/>`)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByOverJoinTests.cs`

**Interfaces:**
- Consumes: `MongoGroupElementTranslator(IEntityType, MongoJoinScope?, MongoGrouping?)` and
  `NativeGroupByBinder.CreateElementTranslator(MongoQueryExpression)` from Task 1;
  `NativeJoinScopeProjectionBinder.ConfirmEntireChain(MongoQueryExpression, MongoJoinScope)`;
  `MongoQueryExpression.AreAllJoinsRowCountPreserving()`.
- Produces:
  - `internal MongoJoinScope? MongoSelectDefinition.GroupByJoinScope { get; set; }`
  - `internal static MongoJoinScope? MongoQueryableMethodTranslatingExpressionVisitor.TryGetGroupByJoinScope(MongoQueryExpression mongoQueryExpression, LambdaExpression keySelector)`
    (pure: no mutation)
  - `private static void NativeGroupByBinder.ConfirmGroupByJoinScope(MongoQueryExpression mongoQ)`

- [ ] **Step 1: Write the failing functional tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByOverJoinTests.cs`, with the license
header and a UTF-8 BOM. Model and context helpers follow `NativeJoinTests` (`Query/NativeJoinTests.cs:2233-2474`).

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native GroupBy composed on a join scope (join-then-group): a reference navigation, an explicit/query-syntax
/// join, a GroupJoin/DefaultIfEmpty left join, a self-join and a two-level chain. Each shape asserts
/// NativeOnly == DriverLinq == a hand-computed expectation over a seed that includes a dangling required FK, a
/// null and a dangling optional FK, and an all-null inner value. Group-then-join is a different ordering, still
/// hard-declined (see NativeGroupByTests.GroupBy_combined_with_Join_*).
/// </summary>
public class NativeGroupByOverJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Owners: Alice (North, Rank 7), Bob (South, Rank null), Cara (North, Rank 3).
    // Orders (Total, Owner, Reviewer, Region):
    //   o1 10 Alice  Bob       North
    //   o2 20 Alice  (null)    North
    //   o3 30 Bob    Alice     South
    //   o4  5 Cara   dangling  North
    //   o5  1 dangling (null)  East     <- dropped by any required Owner join; lowest Total, so first by Total
    // Lines (Order, Sku, Quantity): l1 o1 A 1, l2 o1 B 2, l3 o3 A 4, l4 o5 A 8 (o5's owner dangles).
    private static Seed CreateSeed()
    {
        var alice = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };
        var bob = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = null };
        var cara = new Owner { Id = ObjectId.GenerateNewId(), Name = "Cara", Region = "North", Rank = 3 };

        var o1 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, ReviewerId = bob.Id, Total = 10m, Region = "North" };
        var o2 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, ReviewerId = null, Total = 20m, Region = "North" };
        var o3 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = bob.Id, ReviewerId = alice.Id, Total = 30m, Region = "South" };
        var o4 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = cara.Id, ReviewerId = ObjectId.GenerateNewId(), Total = 5m, Region = "North" };
        var o5 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), ReviewerId = null, Total = 1m, Region = "East" };

        var lines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o1.Id, Sku = "A", Quantity = 1 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o1.Id, Sku = "B", Quantity = 2 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o3.Id, Sku = "A", Quantity = 4 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o5.Id, Sku = "A", Quantity = 8 },
        };

        return new Seed([alice, bob, cara], [o1, o2, o3, o4, o5], lines);
    }

    [Fact]
    public void Required_navigation_key_drops_dangling_rows()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Required_navigation_key_drops_dangling_rows) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count, x.Total)).ToList();
        });

        Assert.Equal([("North", 3, 35m), ("South", 1, 30m)], result);
    }

    [Fact]
    public void Inner_side_accumulator_over_all_null_values_is_null()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Inner_side_accumulator_over_all_null_values_is_null) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, MaxRank = g.Max(o => o.Owner!.Rank) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.MaxRank)).ToList();
        });

        Assert.Equal([("North", (int?)7), ("South", (int?)null)], result);
    }

    [Fact]
    public void Optional_navigation_key_groups_unmatched_rows_under_a_null_key()
    {
        // Reviewer is optional (nullable FK): nav-expansion emits a left join. o2/o5 have no reviewer and o4's
        // dangles; all three stay and group under a null key.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Optional_navigation_key_groups_unmatched_rows_under_a_null_key) + mode);
            return db.Orders
                .GroupBy(o => o.Reviewer!.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([((string?)null, 3), ("Alice", 1), ("Bob", 1)], result);
    }

    [Fact]
    public void Query_syntax_left_join_group_counts_the_unmatched_outer_row()
    {
        // GroupJoin + DefaultIfEmpty, grouped by the outer side (the GroupJoin_GroupBy_Aggregate shape).
        // Every owner has an order, so add an order-less owner for this test.
        var seed = CreateSeed();
        var dora = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dora", Region = "West", Rank = 1 };
        seed = seed with { Owners = [..seed.Owners, dora] };

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Query_syntax_left_join_group_counts_the_unmatched_outer_row) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into grouping
                    from o in grouping.DefaultIfEmpty()
                    group o by w.Name into g
                    select new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("Alice", 2), ("Bob", 1), ("Cara", 1), ("Dora", 1)], result);
    }

    [Fact]
    public void One_to_many_join_multiplies_rows_before_grouping()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(One_to_many_join_multiplies_rows_before_grouping) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId
                    group o by w.Region into g
                    select new { g.Key, Count = g.Count(), Total = g.Sum(x => x.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count, x.Total)).ToList();
        });

        Assert.Equal([("North", 3, 35m), ("South", 1, 30m)], result);
    }

    [Fact]
    public void Self_join_groups_by_the_outer_and_aggregates_the_inner()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Self_join_groups_by_the_outer_and_aggregates_the_inner) + mode);
            return (from o in db.Orders
                    join o2 in db.Orders on o.Id equals o2.Id
                    group o2 by o.Region into g
                    select new { g.Key, Max = g.Max(x => x.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Max)).ToList();
        });

        Assert.Equal([("East", 1m), ("North", 20m), ("South", 30m)], result);
    }

    [Fact]
    public void Composite_key_spanning_both_scopes()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Composite_key_spanning_both_scopes) + mode);
            return db.Orders
                .GroupBy(o => new { o.Region, OwnerName = o.Owner!.Name })
                .Select(g => new { g.Key.Region, g.Key.OwnerName, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Region).ThenBy(x => x.OwnerName)
                .Select(x => (x.Region, x.OwnerName, x.Count)).ToList();
        });

        Assert.Equal([("North", "Alice", 2), ("North", "Cara", 1), ("South", "Bob", 1)], result);
    }

    [Fact]
    public void Two_level_chain_key()
    {
        // OrderLine -> Order (required) -> Owner (required): l4's order has a dangling owner, so it is dropped.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Two_level_chain_key) + mode);
            return db.OrderLines
                .GroupBy(l => new { l.Order!.Owner!.Region, l.Sku })
                .Select(g => new { g.Key.Region, g.Key.Sku, Quantity = g.Sum(l => l.Quantity) })
                .AsEnumerable().OrderBy(x => x.Region).ThenBy(x => x.Sku)
                .Select(x => (x.Region, x.Sku, x.Quantity)).ToList();
        });

        Assert.Equal([("North", "A", 1), ("North", "B", 2), ("South", "A", 4)], result);
    }

    [Fact]
    public void Terminal_count_over_a_navigation_group()
    {
        var seed = CreateSeed();
        var counts = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Terminal_count_over_a_navigation_group) + mode);
            return new List<int> { db.Orders.GroupBy(o => o.Owner!.Region).Count() };
        });

        Assert.Equal([2], counts);
    }

    [Fact]
    public void Having_and_ordering_over_a_navigation_group()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Having_and_ordering_over_a_navigation_group) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Name)
                .Where(g => g.Count() > 0)
                .OrderByDescending(g => g.Sum(o => o.Total))
                .Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
                .AsEnumerable()
                .Select(x => (x.Key, x.Total)).ToList();
        });

        Assert.Equal([("Alice", 30m), ("Bob", 30m), ("Cara", 5m)], result.OrderByDescending(x => x.Total).ThenBy(x => x.Key).ToList());
    }

    [Fact]
    public void Paging_before_a_row_preserving_left_join_group_stays_native()
    {
        // Reviewer is a left-outer reference nav (1:1), so Take(3) commutes with the $lookup:
        // first 3 by Total = o5 (no reviewer), o4 (dangling reviewer), o1 (Bob).
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Paging_before_a_row_preserving_left_join_group_stays_native) + mode);
            return db.Orders
                .OrderBy(o => o.Total).Take(3)
                .GroupBy(o => o.Reviewer!.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([((string?)null, 2), ("Bob", 1)], result);
    }

    [Fact]
    public void Paging_before_a_required_navigation_group_declines_cleanly()
    {
        // Review Focus #2. Correct answer: Take(3) by Total = o5, o4, o1, and the inner join drops o5 (dangling
        // owner), so North = 2. Deferring the Take past the $lookup would page the joined rows (o4, o1, o2) and
        // answer North = 3, silently. So this must decline, never go native.
        var seed = CreateSeed();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Paging_before_a_required_navigation_group_declines_cleanly));
            return db.Orders
                .OrderBy(o => o.Total).Take(3)
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void Paging_before_a_two_level_chain_group_declines_cleanly()
    {
        var seed = CreateSeed();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Paging_before_a_two_level_chain_group_declines_cleanly));
            return db.OrderLines
                .OrderBy(l => l.Quantity).Take(2)
                .GroupBy(l => l.Order!.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void GroupBy_over_a_join_projecting_only_the_inner_side_declines_cleanly()
    {
        // Review Focus #4: the GroupBy parameter is an Owner, not a TransparentIdentifier. Join mode must not
        // engage (the root translator would resolve `w.Region` against Order.Region by name).
        var seed = CreateSeed();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(GroupBy_over_a_join_projecting_only_the_inner_side_declines_cleanly) + mode);
            return db.Orders
                .Join(db.Owners, o => o.OwnerId, w => w.Id, (o, w) => w)
                .GroupBy(w => w.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 3), ("South", 1)], result);
    }

    [Fact]
    public void Post_group_where_after_a_confirmed_join_group_declines_cleanly()
    {
        // Review Focus #5: the grouped Select confirms the join (registering its $lookup), and then the post-group
        // Where declines. The fallback must still be correct with the confirmed (flat _lookup_) document shape.
        // When a later slice makes post-GroupBy Where native, this will throw from DeclinesCleanly's NativeOnly
        // half. That is the signal to flip it to NativeAndParity.
        var seed = CreateSeed();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Post_group_where_after_a_confirmed_join_group_declines_cleanly) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .Where(x => x.Count > 1)
                .AsEnumerable()
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 3)], result);
    }

    private sealed record Seed(Owner[] Owners, Order[] Orders, OrderLine[] OrderLines);

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public int? Rank { get; set; }
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public ObjectId? ReviewerId { get; set; }
        public Owner? Reviewer { get; set; }
        public decimal Total { get; set; }
        public string Region { get; set; } = "";
        public List<OrderLine> OrderLines { get; set; } = [];
    }

    public class OrderLine
    {
        public ObjectId Id { get; set; }
        public ObjectId OrderId { get; set; }
        public Order? Order { get; set; }
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private GroupByOverJoinDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var prefix = TemporaryDatabaseFixtureBase.CreateCollectionName(name);
        var ownersName = prefix + "W" + suffix;
        var ordersName = prefix + "O" + suffix;
        var linesName = prefix + "L" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);
        database.MongoDatabase.GetCollection<OrderLine>(linesName).InsertMany(seed.OrderLines);

        return new GroupByOverJoinDbContext(database, ownersName, ordersName, linesName, mode);
    }

    private sealed class GroupByOverJoinDbContext(
        TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, string linesCollection,
        MongoQueryMode mode)
        : DbContext(new DbContextOptionsBuilder<GroupByOverJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderLine> OrderLines { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(ownersCollection);
                b.HasMany(w => w.Orders).WithOne(o => o.Owner).HasForeignKey(o => o.OwnerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasOne(o => o.Reviewer).WithMany().HasForeignKey(o => o.ReviewerId);
                b.HasMany(o => o.OrderLines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
            });
            modelBuilder.Entity<OrderLine>(b => b.ToCollection(linesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
```

Notes for the implementer:
- `NativeTranslationNotSupportedException` is in `MongoDB.EntityFrameworkCore.Query.NativeTranslation`; add the
  `using` if the build asks for it.
- The expected values are hand-derived from the seed comment at the top. If a native result disagrees with an
  expectation, **do not edit the expectation to match**. Recompute it by hand from the seed and from EF's
  documented join semantics (a required nav is an inner join, an optional nav is a left join, `DefaultIfEmpty`
  keeps unmatched outers). If the expectation is right, the code is wrong. If you are sure the expectation is
  wrong, say so explicitly in your report, with the derivation.
- If a `DeclinesCleanly` test fails because **driver-LINQ itself** throws or returns wrong data (not native),
  report it as a finding and don't work around it. That includes the `DriverLinq`/`Native` halves of
  `Post_group_where_after_a_confirmed_join_group_declines_cleanly`.

- [ ] **Step 2: Run the new tests and confirm which fail before the change**

```bash
S=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/task2; mkdir -p $S
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" 2>&1 | grep -E " error |Error\(s\)" | tail -3
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByOverJoinTests" 2>&1 | tee $S/before.log | tail -30
```
Expected: every `NativeAndParity` test fails with `NativeTranslationNotSupportedException` (they don't go
native yet). The four decline tests (`Paging_before_a_required_navigation_group_declines_cleanly`,
`Paging_before_a_two_level_chain_group_declines_cleanly`,
`GroupBy_over_a_join_projecting_only_the_inner_side_declines_cleanly`,
`Post_group_where_after_a_confirmed_join_group_declines_cleanly`) already pass. They are guards: they have to
keep passing after the change. If any of them fails now, stop and report it; it's a pre-existing driver/fallback
problem.

- [ ] **Step 3: Add `GroupByJoinScope` to `MongoSelectDefinition`**

Next to `JoinScope` (~:719):

```csharp
/// <summary>
/// Set by <c>TranslateGroupBy</c> when the grouped source is a native join scope that is safe to group over
/// (<c>TryGetGroupByJoinScope</c>). <see cref="NativeGroupByBinder"/> then resolves key parts, accumulator operands
/// and element predicates over the join's <c>TransparentIdentifier</c> element through this scope, and confirms the
/// join chain only once the whole grouping has bound. <see langword="null"/> means root-entity resolution (the join,
/// if any, stays an unconfirmed candidate and the query falls back).
/// </summary>
internal MongoJoinScope? GroupByJoinScope { get; set; }
```

Also check whether `MongoSelectDefinition` has a clone/copy method (grep `Clone\|CopyFrom` in the file). If one
copies `JoinScope`, copy `GroupByJoinScope` the same way. Missing it would silently drop join mode in cloned
subqueries.

- [ ] **Step 4: Add the pure eligibility helper in QMTEV**

Next to `IsSingleEligibleNativeJoinScope` (~:722):

```csharp
/// <summary>
/// The join scope a <c>GroupBy</c> may resolve through, or <see langword="null"/> to keep root-entity resolution.
/// Pure: unlike <see cref="IsSingleEligibleNativeJoinScope"/> it never defers recorded ops, so it's safe to call
/// before the grouping has bound. Called before <c>IsGroupBy</c> is set.
/// </summary>
/// <remarks>
/// Paging recorded ahead of the grouped join is kept only when every join is 1:1 (a left-outer reference
/// navigation), so it commutes with the <c>$lookup</c>. Any other paging declines instead of being deferred past
/// the <c>$lookup</c>. Deferral is acceptable for a projection (it differs only for dangling references), but here
/// it would silently change group counts. Chains decline on any paging (the snapshot gap at the bare-value arm of
/// <see cref="TranslateSelect"/>). The key selector's parameter must be the join's <c>TransparentIdentifier</c>. A
/// result selector that already projected one side hands GroupBy an entity parameter that
/// <see cref="MongoGroupElementTranslator"/> can't route.
/// </remarks>
internal static MongoJoinScope? TryGetGroupByJoinScope(MongoQueryExpression mongoQueryExpression, LambdaExpression keySelector)
{
    var select = mongoQueryExpression.Select;
    if (select.JoinScope is not { } scope
        || !keySelector.Parameters[0].Type.IsTransparentIdentifierType()
        || scope.Levels.Count != mongoQueryExpression.Joins.Count
        || select.HasUnsupportedOperator
        || select.HasTerminalOperator
        || select.UnwindSource != null
        || select.Cardinality != null
        || mongoQueryExpression.Joins.Any(j => j.Lookup is null))
    {
        return null;
    }

    if (select.HasPaging
        && (scope.Levels.Count > 1
            || select.JoinInnerAccessConfirmed
            || !mongoQueryExpression.AreAllJoinsRowCountPreserving()))
    {
        return null;
    }

    return scope;
}
```

Check each member's exact name and meaning before relying on it, by reading its definition in
`MongoSelectDefinition.cs` / `MongoQueryExpression*.cs`: `HasPaging`, `JoinInnerAccessConfirmed`,
`AreAllJoinsRowCountPreserving`, `UnwindSource`, `Cardinality`, `HasTerminalOperator`. `IsSingleEligibleNativeJoinScope`
uses all of them, so they exist. Confirm that `HasPaging` covers paging recorded both before and after the join.
If it covers only one, use the `HasPagingRecordedBeforeAnyJoin` / `HasPagingRecordedAfterAJoin` pair so that
*any* recorded paging is covered.

- [ ] **Step 5: Call it from `TranslateGroupBy`**

In `TranslateGroupBy` (~:1842), after `hasFinalizedPriorGrouping` is computed and **before**
`mongoQueryExpression.Select.IsGroupBy = true;`:

```csharp
// Decided before IsGroupBy is set (which makes HasTerminalOperator true). A nested GroupBy over a finalized
// grouping resolves against the prior stage's alias, never a join scope.
var groupByJoinScope = hadTerminalGrouping || hasFinalizedPriorGrouping
    ? null
    : TryGetGroupByJoinScope(mongoQueryExpression, keySelector);
```

Then, in the final `else` branch, immediately before the `resultSelector != null || !TryBindGroupKey(...)`
check:

```csharp
mongoQueryExpression.Select.GroupByJoinScope = groupByJoinScope;
```

Update the method's leading comment (`Native supports only GroupBy(key).Select(aggregate)`) to mention the join
scope in one clause.

- [ ] **Step 6: Use the scope in the binder and confirm at commit**

In `NativeGroupByBinder.cs`:

1. `CreateElementTranslator` now passes the scope:
   ```csharp
   private static MongoGroupElementTranslator CreateElementTranslator(MongoQueryExpression mongoQ)
       => new(mongoQ.CollectionExpression.EntityType, mongoQ.Select.GroupByJoinScope, mongoQ.Select.PriorGrouping);
   ```
   If Task 1 left `TryBindGroupTerminalAggregate`/`TryBindGroupWherePredicate` on an explicit
   `priorGrouping: null` construction, pass `mongoQ.Select.GroupByJoinScope` there as the `joinScope` argument.
2. Add:
   ```csharp
   // Registers the grouped join chain's $lookup(s) once the grouping has fully bound. Deferred to here, the commit
   // point, so a decline leaves the join an unconfirmed candidate (Route stays Fallback) and the driver-LINQ
   // fallback keeps its unconfirmed document shape. Skipped when an earlier operator already forced a fallback, for
   // the same reason.
   private static void ConfirmGroupByJoinScope(MongoQueryExpression mongoQ)
   {
       if (mongoQ.Select.GroupByJoinScope is { } scope && !mongoQ.Select.HasUnsupportedOperator)
           NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQ, scope);
   }
   ```
3. Call `ConfirmGroupByJoinScope(mongoQ);` in `TryBindGroupProjection` right before its final `return true;`,
   after the `select.AddProjection` loop. Call it in `TryBindGroupTerminalAggregate` right before its
   success `return true;`. Read that method end to end, and put the call only on the path where every gate has
   passed and the select has been committed.
4. Check `TryBindGroupWherePredicate` and the `PendingGroupOrderings` path: they only stage state that
   `TryBindGroupProjection`/`TryBindGroupTerminalAggregate` commit later, so they must **not** confirm.

- [ ] **Step 7: Turn on the cref in `MongoGroupElementTranslator`**

Change the plain-text `<c>MongoSelectDefinition.GroupByJoinScope</c>` in its summary to
`<see cref="MongoSelectDefinition.GroupByJoinScope"/>`.

- [ ] **Step 8: Run the new functional tests**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | grep -E "warn|Error\(s\)" | tail -3
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByOverJoinTests" 2>&1 | tee $S/after.log | tail -30
```
Expected: all 15 pass.

If `Paging_before_a_required_navigation_group_declines_cleanly` fails because the query **went native**, the
paging gate is wrong, and that is a silent-wrong-data bug. Fix the gate; don't touch the test.

- [ ] **Step 9: Prove each guard discriminates, by mutation**

For each mutation below: apply it, rebuild, run the named test(s), confirm they **fail**, then revert. Record the
result lines in `$S/mutations.txt`.

| Mutation | Test(s) that must fail |
|---|---|
| In `TryGetGroupByJoinScope`, delete the whole `if (select.HasPaging && ...)` block | `Paging_before_a_required_navigation_group_declines_cleanly`, `Paging_before_a_two_level_chain_group_declines_cleanly` |
| In `TryGetGroupByJoinScope`, delete the `IsTransparentIdentifierType()` condition, **and** in `MongoGroupElementTranslator.TryTranslateCore` replace `!parameters[0].Type.IsTransparentIdentifierType()` with `false` | `GroupBy_over_a_join_projecting_only_the_inner_side_declines_cleanly` (if it doesn't fail, report which layer still blocks it) |
| In `ConfirmGroupByJoinScope`, make the body empty | every `NativeAndParity` test (expected: `NativeTranslationNotSupportedException`, not wrong data. That shows the unconfirmed-candidate gate is fail-closed) |

Revert every mutation and rebuild. `git diff --stat` should show only the intended changes.

- [ ] **Step 10: Regression-check the neighbouring suites**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build 2>&1 | tail -1
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build 2>&1 | tail -1
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -1
```
Expected: unit and functional have 0 failures. The GroupBy spec class **is expected to have failures now**:
MQL baseline mismatches on the 6 target tests, and "no exception thrown" on the knock-on
`AssertTranslationFailed` tests. Task 3 fixes those; don't touch the spec tests in this task. Save the failing
test list: `... 2>&1 | grep -E "^\s+Failed " | sort > $S/spec-groupby-failures.txt`. The implementer's report
must include it.

Pay attention to `NativeGroupByTests.GroupBy_over_a_joined_source_runs_correctly_under_native` (join with
`(o, x) => o`, then GroupBy): the GroupBy parameter is an `Order`, not a transparent identifier, so it must stay
on the fallback, unchanged.

- [ ] **Step 11: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByOverJoinTests.cs
git commit -m "EF-322: native GroupBy over a join scope (join-then-group)"
```

---

### Task 3: Specification-suite updates and docs

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs`
- Modify (only if the full-suite run in Task 4 shows it): other `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/*MongoTest.cs`
- Modify: `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` ("Scope" section)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` ("Durable invariants")

**Interfaces:**
- Consumes: Task 2's behavior. No code interfaces.

- [ ] **Step 1: Classify every GroupBy spec failure from Task 2 Step 10, by name**

```bash
S=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/task3; mkdir -p $S
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" --logger "trx;LogFileName=$S/default.trx" 2>&1 | tail -1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" --logger "trx;LogFileName=$S/nativeonly.trx" 2>&1 | tail -1
```
List each failing test in each mode with the first line of its message (parse the TRX, e.g. with a small
Python `xml.etree` script). Every failure must fall into one of these buckets:
- **(a) MQL baseline mismatch** (`AssertMql` / baseline text differs). Expected for the 6 targets.
- **(b) "Assert.Throws… No exception was thrown"** on an `AssertTranslationFailed` override. The query now
  succeeds.
- **(c) anything else.** Stop and report it. It's a regression.

Classify by the message; don't infer a bucket from the test's name.

- [ ] **Step 2: Flip the bucket-(b) overrides**

For each bucket-(b) test, look at the upstream base body
(`/Users/arthur.vickers/code/efcore/test/EFCore.Specification.Tests/Query/NorthwindGroupByQueryTestBase.cs`,
EF main; EF10 is the same unless the build says otherwise). Confirm it asserts **data** (`AssertQuery`
against the L2O oracle), then:
- If it succeeds in **both** modes: replace
  ```csharp
  // Fails: GroupBy issue EF-149
  await AssertTranslationFailed(() => base.X(async));
  AssertMql();
  ```
  with
  ```csharp
  await base.X(async);

  AssertMql();
  ```
  Remove the `// Fails:` comment.
- If it succeeds in default mode but still throws under NativeOnly (the spike showed this for
  `GroupBy_Min_Where_optional_relationship(_2)`: the grouped Select confirms, then a post-group `Where`
  declines), use the existing mode-split pattern from this file (see
  `GroupBy_aggregate_projecting_conditional_expression` ~:254):
  ```csharp
  if (MongoSpecTestHelpers.IsNativeOnly)
  {
      // Post-group Where isn't native yet; the fallback runs the confirmed-join shape correctly.
      await AssertTranslationFailed(() => base.X(async));
      AssertMql();
  }
  else
  {
      await base.X(async);
      AssertMql();
  }
  ```

- [ ] **Step 3: Regenerate baselines for exactly the affected tests**

Build, then rewrite baselines scoped to the bucket-(a) and bucket-(b) test names. The filter needs full names:
`FullyQualifiedName~NorthwindGroupByQueryMongoTest.GroupBy_required_navigation_member_Aggregate|FullyQualifiedName~...`.
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" 2>&1 | grep -E "Error\(s\)"
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "<the scoped filter>" 2>&1 | tail -1
git diff --stat tests/MongoDB.EntityFrameworkCore.SpecificationTests
```
The rewrite run reports failures by design. Read the `git diff` on the spec file. Each rewritten baseline
should show `$lookup` → `$unwind` → `$group`, reading `_lookup_<Nav>.<field>` for inner-side leaves. Look for
corruption or misplaced output, which the rewriter is known to produce. For a mode-split override, the baseline
that gets rewritten is the one for the mode you ran (default). If the NativeOnly branch's `AssertMql()` needs a
baseline, rerun with `MONGODB_EF_NATIVE_ONLY=1` and the same scoped filter.

- [ ] **Step 4: Re-run the class in both modes; recount by name**

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" 2>&1 | grep -E "Error\(s\)"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" --logger "trx;LogFileName=$S/nativeonly-after.trx" 2>&1 | tail -1
```
Expected: default mode has 0 failures. Under NativeOnly the only remaining failures are
`GroupBy_count_filter` and `GroupBy_selecting_grouping_key_list` (× async = 4). If any of the 6 targets still
fails under NativeOnly, report it with its message. It isn't done. List the NativeOnly failures **by name**
from the TRX; don't just report a count.

- [ ] **Step 5: Update docs**

1. In `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md`, "Scope" section: add a
   short paragraph after the one listing the 6 deferred methods. It should say they were delivered (plus which
   knock-on `AssertTranslationFailed` tests went native, by name from Step 1). Point to this plan and state the
   paging decision in one sentence (paging ahead of a non-1:1 grouped join declines rather than defers).
2. In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, under "Durable invariants", add one bullet in the
   file's terse style:
   ```markdown
   - **GroupBy over a join scope** (`MongoSelectDefinition.GroupByJoinScope`, decided purely in `TranslateGroupBy`):
     requires a `TransparentIdentifier` key parameter (a result selector that projected one side must not resolve
     by name against the root entity); the chain is confirmed only at the binder's commit point. Paging recorded
     ahead of a non-1:1 grouped join declines — deferring it past the `$lookup` (fine for projections) silently
     changes group counts. See `NativeGroupByOverJoinTests`.
   ```

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-322: re-baseline GroupBy spec tests now native over join scopes; document the invariant"
```

---

### Task 4: Full-suite, three-version verification

Changes to allow-lists and dispatch have regressed tests far outside the class being worked on before
(EF-436), so this task runs the **full** suites on all three EF versions.

**Files:**
- Modify only if failures are found: whichever spec overrides are affected (baseline-only), or `src/` for real
  bugs. Either kind of change needs a written justification in the report.

- [ ] **Step 1: Build all three configurations**

Run: `for c in EF8 EF9 EF10; do dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" 2>&1 | grep -E "Error\(s\)|warn" | tail -2; done`
Expected: `0 Error(s)` each, and no new warnings.

- [ ] **Step 2: Run unit + functional + specification suites for EF8, EF9, EF10**

Use the `/test-all` skill if it's available to you. Otherwise, for each `c` in `EF8 EF9 EF10`, run
```bash
S=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/task4; mkdir -p $S
dotnet test MongoDB.EFCoreProvider.sln -c "Debug $c" --no-build --logger "trx;LogFilePrefix=$c" --results-directory $S/$c 2>&1 | grep -E "Passed!|Failed!"
```
Expected: 0 failures for every project and every version. The base commit `fdd36a39` was green on all three
(commit `b660f6d8` fixed the last EF8/EF9-only failures), so any failure is new.

- [ ] **Step 3: Triage any failure by name**

For each failure, record the name, version, and first message line, then classify it:
- **Baseline-only drift** in another class where a join-then-group query now goes native. Confirm data
  assertions ran and passed first (the `AssertMql` call is after `await base...`). Then regenerate that test's
  baseline with a scoped `EF_TEST_REWRITE_BASELINES=1` run for **that version**. If EF8/EF9 differ from EF10,
  use the file's existing `#if EF8 || EF9` pattern.
- **EF8/EF9-only data or exception failure.** EF8/EF9 go through the internal `LeftJoin` shim (see the
  Query `AGENTS.md` "Allowed method sources" pitfall), so a left-join key may reach the binder in a different
  shape. Find the root cause (don't guess). If the fix is a decline, add it to `TryGetGroupByJoinScope` and add a
  functional test that pins it.
- **Anything else**: stop and report.

- [ ] **Step 4: NativeOnly delta on the full EF10 spec suite**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --logger "trx;LogFileName=$S/nativeonly-full-after.trx" 2>&1 | tail -1
```
Then build and run the same command in a detached worktree at the base commit, to get the "before" set:
```bash
git worktree add --detach $S/base fdd36a39
(cd $S/base && dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | grep "Error(s)" && \
  MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build --logger "trx;LogFileName=$S/nativeonly-full-before.trx" 2>&1 | tail -1)
git worktree remove --force $S/base
```
Diff the two failure sets **by test name**. Report PASS→FAIL (must be empty) and FAIL→PASS (the tests that newly
went native; every one should be a join-then-group shape). Re-sum any count you state from the name lists
themselves.

- [ ] **Step 5: Commit any fixes**

```bash
git add -A tests src
git status --short   # confirm nuget.config and untracked scratch are NOT staged
git commit -m "EF-322: re-baseline cross-version specs for native GroupBy over join scopes"
```
Skip this step if nothing changed.
