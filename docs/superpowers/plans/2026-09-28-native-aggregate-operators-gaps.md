# Native coverage for NorthwindAggregateOperatorsQueryMongoTest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the 24 `NorthwindAggregateOperatorsQueryMongoTest` tests that still fall back to driver LINQ (48 sync+async runs, EF10) go through the native translator, so the class passes under `MONGODB_EF_NATIVE_ONLY=1`.

**Architecture:** Six independent gaps, each closed by widening one existing native recognizer:

- selector-less aggregates over a bare scalar `Select`;
- `Where`-predicate `Contains` recognizer holes (set interfaces, empty inline list, tuple needle, a closed `Enumerable.Where` over a local collection);
- a reference-type upcast around a DTO projection;
- `string.FirstOrDefault()` as a client-applied string leaf;
- reducers after a row-count-preserving `$lookup`;
- `Queryable.Contains` as a terminal operator (bound as `Any(e => e == item)`).

The one new component is a small preprocessing visitor that folds a closed `Enumerable.Where(localCollection, pred)` into a constant, or into a runtime query parameter via EF's own `QueryCompilationContext.RegisterRuntimeParameter`. Every other change is a local edit to an existing binder, matcher or renderer.

**Tech Stack:** C#, EF Core 8/9/10 (build configurations `Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit (plain `Assert.*`).

**Spec:** No separate spec document. The investigation that produced this plan is summarized in **Background** below. Its decline-site evidence came from temporarily instrumenting `MongoSelectDefinition.MarkNotNativelyRepresentable` and `VisitProjectedQuery`, which were reverted afterwards.

## Background: the investigation (the "spec")

Run on `Debug EF10`: normal mode passes 423/423. With `MONGODB_EF_NATIVE_ONLY=1`, 48 fail (24 distinct tests). Decline site per test:

| # | Tests | LINQ shape | Decline site |
|---|---|---|---|
| A | `Sum_with_no_arg`, `Sum_with_no_arg_empty`, `Min_with_no_arg`, `Max_with_no_arg`, `Average_with_no_arg`, `Sum_with_binary_expression`, `Average_with_binary_expression`, `Sum_with_no_data_cast_to_nullable`, `Min/Max_with_non_matching_types_in_projection_introduces_explicit_cast`, `Average_with_non_matching_types_in_projection_doesnt_produce_second_explicit_cast` | `Select(o => expr).Sum()` (no selector) | `NativeCardinalityBinder.TryBindAggregate`: `selector is null` ⇒ `return false` |
| B1 | `IReadOnlySet_Contains_with_parameter`, `IImmutableSet_Contains_with_parameter` | `IReadOnlySet<T>.Contains` / `IImmutableSet<T>.Contains` instance call | `MongoExpressionTranslator.TryMatchContainsMethod` whitelist |
| B2 | `Contains_with_local_collection_empty_inline` | `!(new List<string>().Contains(c.CustomerID))`: a bare `NewExpression` | `TranslateInValues` has no arm for it |
| B3 | `Contains_with_local_tuple_array_closure` | `Enumerable.Contains(<param Tuple<int,int>[]>, new Tuple<int,int>(o.OrderID, o.ProductID))` | Contains arm's computed-needle gate rejects tuples |
| B4 | `Contains_with_local_enumerable_inline`, `Contains_with_local_enumerable_inline_closure_mix` | `Enumerable.Contains(Enumerable.Where(<constant List or param List>, e => e != null), c.CustomerID)` | `TranslateInValues` returns null for a method call |
| C | `Return_type_of_singular_operator_is_preserved` (EF9+) | selector body `Convert(new CustomerIdAndCityDto{...}, CustomerIdDto)` | `NativeProjectionBinder.TryPopulateNativeProjection` |
| D | `String_FirstOrDefault_in_projection_does_not_do_client_eval` | `Select(c => c.CustomerID.FirstOrDefault())` | same, since `IsStringSequenceMaterializationCall` excludes `FirstOrDefault` |
| E | `OfType_Select`, `OfType_Select_OfType_Select` | `OrderBy(...).Select(o => o.Customer.City).First()`, i.e. an optional (left-outer) reference nav | `TryBindReducer`: `HasConfirmedJoinLookup` ⇒ `return false` |
| F | `Contains_top_level`, `Contains_over_entityType_should_rewrite_to_identity_equality`, `Contains_over_entityType_with_null_should_rewrite_to_false` | `Queryable.Contains` terminal | `TranslateContains` only reshapes; `Contains` not in `IsNativeRepresentableSlotOperator` |

## Global Constraints

- Build and pass on all three configurations: `Debug EF8`, `Debug EF9`, `Debug EF10`. `Return_type_of_singular_operator_is_preserved` is `#if !EF8`.
- **Preserve file BOMs** when editing (repo `AGENTS.md`). New files: copy the license header from a sibling file in the same folder and match that sibling's BOM (test `.cs` files under `FunctionalTests/Query/` start with a UTF-8 BOM).
- `src/` is nullable-enabled. Annotate new members.
- "Went native" is proven **only** by `MongoQueryMode.NativeOnly` succeeding. MQL shape never proves it (`Query/AGENTS.md`). Every new native shape gets a `NativeOnly` test plus parity against `DriverLinq` (`NativeModeAssert.NativeAndParity`) or a hand-verified expected value.
- A recognizer must not mutate then decline: stage into locals, commit only after every gate passes (`Query/AGENTS.md`).
- Commit messages start with `EF-322: ` (current branch `EF-322-Native-LINQ-rebased`).
- Recommended test environment: `MONGODB_URI` and `ATLAS_URI` **unset**, so TestContainers boots an isolated container (repo `AGENTS.md`). If they are set, the tests still run against the external server.
- MQL baselines are regenerated, never hand-edited: `EF_TEST_REWRITE_BASELINES=1` + a tight `--filter`, then `git diff`, rebuild, rerun without the var (SpecificationTests `AGENTS.md`).
- xUnit filter gotcha: `FullyQualifiedName~X(async: False)` does **not** match. Use `FullyQualifiedName=<Namespace>.<Class>.<Method>`, which runs both async variants.

## Common commands (referenced by every task)

```bash
# from repo root
BUILD='dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"'
SPEC=tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj
FUNC=tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj
UNIT=tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj
AGG=MongoDB.EntityFrameworkCore.SpecificationTests.Query.NorthwindAggregateOperatorsQueryMongoTest

# one spec test, native-only (the "went native" gate)
MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC -c "Debug EF10" --no-build --filter "FullyQualifiedName=$AGG.<Method>"
# regenerate its MQL baseline (reports FAILED when it rewrote; rebuild + rerun to confirm green)
EF_TEST_REWRITE_BASELINES=1 dotnet test $SPEC -c "Debug EF10" --no-build --filter "FullyQualifiedName=$AGG.<Method>"
# per-task regression sweep
dotnet test $FUNC -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
dotnet test $SPEC -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query" 2>&1 | tail -3
```

**Native-only regression rule.** Before Task 1, record the baseline failure count of the last command (native-only spec Query sweep) in the task notes. After each task, the count must go **down** by exactly the tests the task targets (×2 for async) and nothing else may newly fail. Diff the failing-test lists (`--logger "trx;LogFileName=..."` + the extraction script in Task 0) to check.

## Review Focus

1. **Parameter value changes between executions of a cached query.** A folded `list.Where(e => e != null).Contains(x)` whose `list` is a captured variable must see the new list on the second execution, not a value frozen at compile time. Pinned in Task 4 (`Filtered_parameter_collection_is_re_evaluated_per_execution`).
2. **Empty input for a selector-less aggregate.** Non-nullable `Min()/Max()/Average()` over zero rows throws `InvalidOperationException`; the nullable forms return `null`; `Sum()` returns `0`. Must match LINQ-to-objects. Pinned in Task 1.
3. **Paging before a terminal `Contains`.** `OrderBy(..).Select(..).Take(1).Contains(v)` must test only the first row. Pinned in Task 8 (`Take_before_Contains_only_tests_the_paged_rows`).
4. **First row has a null or dangling FK.** `OrderBy(..).Select(p => p.Customer!.City).First()` where the first row's FK is null or points nowhere must return `null` for that row, not skip it. Pinned in Task 7.
5. **A client-computed projection followed by an aggregate/Contains.** A shape whose shaper does client work on top of the pushed-down value (a client method, a string-sequence leaf) must decline, not aggregate or compare the raw field. Pinned in Task 1 (`Aggregate_over_client_method_projection_declines`) and Task 8 (`Contains_over_computed_projection_declines_cleanly`).

---

### Task 0: Capture the baseline

**Files:** none (records numbers only).

- [ ] **Step 1: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → expect `0 Error(s)`.

- [ ] **Step 2: Record the native-only failure list for the target class and for the whole Query spec sweep**

```bash
S=$(mktemp -d)
MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindAggregateOperatorsQueryMongoTest" --logger "trx;LogFileName=$S/agg.trx" | tail -2
MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Query" --logger "trx;LogFileName=$S/query.trx" | tail -2
python3 - "$S/query.trx" <<'EOF' > $S/baseline-native-failures.txt
import sys, xml.etree.ElementTree as ET
ns='{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
for u in ET.parse(sys.argv[1]).getroot().iter(ns+'UnitTestResult'):
    if u.get('outcome')=='Failed': print(u.get('testName'))
EOF
sort -o $S/baseline-native-failures.txt $S/baseline-native-failures.txt; wc -l $S/baseline-native-failures.txt; echo $S
```

Expected: the target class shows `Failed: 48, Passed: 375`. Keep `$S` for later diffs, re-running the same script with a new trx after each task.

---

### Task 1: Selector-less Sum/Min/Max/Average over a bare scalar Select

Closes gap **A** (11 tests). Also adds the `TryGetBareServerValueProjection` helper that Task 8 reuses.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` (`TryBindAggregate` signature and operand block, near lines 118 and 236–251; new helper)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`BindAggregateOrFallback`, ~line 2479)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectorlessAggregateTests.cs`
- Modify (baselines): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Produces: `internal static MongoProjection? NativeCardinalityBinder.TryGetBareServerValueProjection(MongoQueryExpression mongoQ, Expression sourceShaper)`. It returns the single `_v`-aliased projection when the source is a bare, server-computed scalar `Select` that the shaper reads back unchanged, else `null`.
- Produces: `TryBindAggregate(..., Type resultType, MongoProjection? bareSourceProjection = null)` (new optional trailing parameter).

- [ ] **Step 1: Write the failing functional tests**

Create `NativeSelectorlessAggregateTests.cs` (license header + BOM copied from `NativeCardinalityTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A selector-less Sum()/Min()/Max()/Average() reducing the value a preceding bare scalar Select projected
/// (<c>Select(e =&gt; e.Value).Sum()</c>) — EF's Northwind <c>*_with_no_arg</c> family. The aggregate reduces the
/// Select's own <c>$project</c> output alias.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSelectorlessAggregateTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
        public int? NullableValue { get; set; }
    }

    private IMongoCollection<Row> Seed(string name, params (int Value, int? NullableValue)[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        if (rows.Length > 0)
            collection.InsertMany(rows.Select(r => new Row { Id = ObjectId.GenerateNewId(), Value = r.Value, NullableValue = r.NullableValue }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    private static readonly (int, int?)[] OneTwoThree = [(1, 10), (2, null), (3, 30)];

    private List<T> Run<T>(string name, (int, int?)[] rows, Func<IQueryable<Row>, T> query)
    {
        var collection = Seed(name, rows);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return [query(db.Entities)];
        });
    }

    [Fact]
    public void Sum_over_bare_member_select_goes_native()
        => Assert.Equal([6], Run(nameof(Sum_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Sum()));

    [Fact]
    public void Min_over_bare_member_select_goes_native()
        => Assert.Equal([1], Run(nameof(Min_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Min()));

    [Fact]
    public void Max_over_bare_member_select_goes_native()
        => Assert.Equal([3], Run(nameof(Max_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Max()));

    [Fact]
    public void Average_over_bare_member_select_goes_native()
        => Assert.Equal([2.0], Run(nameof(Average_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Average()));

    [Fact]
    public void Sum_over_computed_select_goes_native()
        => Assert.Equal([12], Run(nameof(Sum_over_computed_select_goes_native), OneTwoThree, q => q.Select(e => e.Value * 2).Sum()));

    [Fact]
    public void Average_over_widening_cast_select_goes_native()
        => Assert.Equal([2.0], Run(nameof(Average_over_widening_cast_select_goes_native), OneTwoThree,
            q => q.Where(e => e.Value > 0).OrderBy(e => e.Value).Select(e => (long)e.Value).Average()));

    [Fact]
    public void Max_over_nullable_select_ignores_nulls()
        => Assert.Equal([30], Run(nameof(Max_over_nullable_select_ignores_nulls), OneTwoThree, q => q.Select(e => e.NullableValue).Max()));

    // Review Focus 2: empty-input contract.
    [Fact]
    public void Sum_over_empty_nullable_cast_select_returns_zero()
        => Assert.Equal([0], Run(nameof(Sum_over_empty_nullable_cast_select_returns_zero), [],
            q => q.Select(e => (int?)e.Value).Sum()));

    [Fact]
    public void Max_over_empty_nullable_select_returns_null()
        => Assert.Equal([null], Run(nameof(Max_over_empty_nullable_select_returns_null), [],
            q => q.Select(e => e.NullableValue).Max()));

    [Fact]
    public void Min_over_empty_non_nullable_select_throws_under_NativeOnly()
    {
        var collection = Seed(nameof(Min_over_empty_non_nullable_select_throws_under_NativeOnly));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<InvalidOperationException>(() => db.Entities.Select(e => e.Value).Min());
    }

    // Review Focus 5: the shaper does client work on top of the pushed-down value — must NOT aggregate the raw field.
    private static int TimesTen(Row row) => row.Value * 10;

    [Fact]
    public void Aggregate_over_client_method_projection_declines()
    {
        var collection = Seed(nameof(Aggregate_over_client_method_projection_declines), OneTwoThree);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Select(e => TimesTen(e)).Sum());
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test $FUNC -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectorlessAggregateTests"`

Expected: every `*_goes_native`, `*_returns_*` and `Max_over_nullable_*` test fails with `NativeTranslationNotSupportedException: Query projects a non-entity result…`. `Min_over_empty_non_nullable_select_throws_under_NativeOnly` also fails, because the exception is the wrong type. `Aggregate_over_client_method_projection_declines` should already pass. If it doesn't, stop and report: that means the client-method shape reaches a different path than assumed.

- [ ] **Step 3: Add the helper and the operand arm in `NativeCardinalityBinder.cs`**

Add `using Microsoft.EntityFrameworkCore.Query;` (for `ProjectionBindingExpression`). Add this method directly after `TryBindReducer`:

```csharp
    /// <summary>
    /// The single server-computed value a preceding bare scalar <c>Select</c> projected — <c>Select(o =&gt; o.OrderID)</c>,
    /// <c>Select(o =&gt; o.OrderID * 2)</c>, <c>Select(o =&gt; (long)o.OrderID)</c> — when <paramref name="sourceShaper"/>
    /// reads exactly that value back with no client-side computation layered on top; otherwise
    /// <see langword="null"/>. A selector-less terminal (<c>Sum()</c>/<c>Min()</c>/<c>Max()</c>/<c>Average()</c>,
    /// <c>Contains(item)</c>) reduces this value.
    /// </summary>
    /// <remarks>
    /// The shaper check is what rejects a client-evaluated projection: a client method call or other client
    /// computation leaves a non-<see cref="ProjectionBindingExpression"/> node in the shaper. The string-sequence
    /// leaf is the one client-applied leaf that ERASES its call from the shaper (it is registered as one projection
    /// member — see <see cref="MongoSelectDefinition.HasStringSequenceProjectionLeaf"/>), so it is excluded by flag.
    /// </remarks>
    internal static MongoProjection? TryGetBareServerValueProjection(MongoQueryExpression mongoQ, Expression sourceShaper)
    {
        var select = mongoQ.Select;
        if (select.Grouping != null
            || select.HasTerminalOperator
            || select.HasStringSequenceProjectionLeaf
            || select.Projection is not [{ Alias: NativeProjectionBinder.SyntheticBareProjectionAlias } projection])
        {
            return null;
        }

        while (sourceShaper is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            sourceShaper = convert.Operand;

        return sourceShaper is ProjectionBindingExpression ? projection : null;
    }
```

Change the `TryBindAggregate` signature (add the last parameter and a `<param>` doc line):

```csharp
    internal static bool TryBindAggregate(
        MongoQueryExpression mongoQ,
        MongoAggregateOperator op,
        LambdaExpression? selector,
        LambdaExpression? predicate,
        Type resultType,
        MongoProjection? bareSourceProjection = null)
```

In the operand block, insert a new arm between the `isPostGroupBySelectlessMinMax` arm and the `else if (selector is null || !translator.TryTranslateValue(...))` arm:

```csharp
            else if (selector is null && bareSourceProjection is { } bareSource)
            {
                // Selector-less Sum()/Min()/Max()/Average() over a preceding bare scalar Select (EF's
                // Sum_with_no_arg family): reduce that Select's own $project output field. MongoSelectLowerer emits
                // the $project (its step 6) before this aggregate's $group (step 7), so "$_v" is in scope — the
                // same alias-reference technique the isPostGroupBySelectlessMinMax arm above uses.
                operand = new MongoElementRefExpression(bareSource.Alias, bareSource.Expression.Type);
            }
```

- [ ] **Step 4: Pass the bare projection from `BindAggregateOrFallback`**

In `MongoQueryableMethodTranslatingExpressionVisitor.BindAggregateOrFallback` replace the body with:

```csharp
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        var bareSource = selector is null && predicate is null
            ? NativeCardinalityBinder.TryGetBareServerValueProjection(mongoQ, source.ShaperExpression)
            : null;
        if (!NativeCardinalityBinder.TryBindAggregate(mongoQ, op, selector, predicate, resultType, bareSource))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return ReshapeShaperExpression(source, resultType);
```

- [ ] **Step 5: Run the functional tests, verify they pass**

Run: build, then `dotnet test $FUNC -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectorlessAggregateTests"` → all pass.

- [ ] **Step 6: Verify the 10 spec tests go native, then regenerate their baselines**

For each `M` in `Sum_with_no_arg Sum_with_no_arg_empty Min_with_no_arg Max_with_no_arg Average_with_no_arg Sum_with_binary_expression Average_with_binary_expression Sum_with_no_data_cast_to_nullable Min_with_non_matching_types_in_projection_introduces_explicit_cast Max_with_non_matching_types_in_projection_introduces_explicit_cast Average_with_non_matching_types_in_projection_doesnt_produce_second_explicit_cast`:

1. `MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC ... --filter "FullyQualifiedName=$AGG.$M"`. It must fail **only** on `AssertMql` (a baseline mismatch), never with `NativeTranslationNotSupportedException`.
2. `EF_TEST_REWRITE_BASELINES=1 dotnet test $SPEC ... --filter "FullyQualifiedName=$AGG.$M"`.

Then `git diff` the spec file. Expected new shape, e.g. for `Sum_with_no_arg`: `Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "v" : { "$sum" : "$_v" } } }`. Only these tests' `AssertMql` blocks may change. Rebuild and rerun all of them in **both** modes (with and without `MONGODB_EF_NATIVE_ONLY=1`) → green.

- [ ] **Step 7: Regression sweep**

Run the per-task sweep. Native-only failure count drops by 22 (11 methods × 2); no new failures (diff vs. baseline list).

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs \
  src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectorlessAggregateTests.cs \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindAggregateOperatorsQueryMongoTest.cs
git commit -m "EF-322: native selector-less Sum/Min/Max/Average over a bare scalar Select"
```


---

### Task 2: `Contains` over set interfaces and an empty inline list

Closes gaps **B1** and **B2** (3 tests).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (`TryMatchContainsMethod` ~line 376–390; `TranslateInValues` ~line 514)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` (append tests)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeLocalCollectionContainsTests.cs`
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Produces: the functional test class `NativeLocalCollectionContainsTests`, with helpers `Seed(string name)` and `Names(string name, Func<IQueryable<Row>, IQueryable<Row>> query)`. Tasks 3 and 4 append to it.

- [ ] **Step 1: Write failing unit tests** (append to `MongoExpressionTranslatorTests`, next to "Test 15"; add `using System.Collections.Immutable;` if absent)

```csharp
    [Theory]
    [InlineData(typeof(ISet<int>))]
    [InlineData(typeof(IReadOnlySet<int>))]
    [InlineData(typeof(IImmutableSet<int>))]
    public void Instance_set_interface_contains_is_matched(Type setInterface)
    {
        var set = Expression.Parameter(setInterface, "set");
        var item = Expression.Parameter(typeof(int), "item");
        var call = Expression.Call(set, setInterface.GetMethod("Contains", [typeof(int)])!, item);

        Assert.True(MongoExpressionTranslator.TryMatchContainsMethod(call, out var collection, out var matchedItem));
        Assert.Same(set, collection);
        Assert.Same(item, matchedItem);
    }

    [Fact]
    public void Negated_contains_over_empty_inline_list_translates_to_negated_in()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        var body = PredicateBody<Customer>(c => !new List<int>().Contains(c.Age));

        Assert.True(translator.TryTranslate(body, out var result));
        // Must be an EMPTY haystack; negation handled by the negator/renderer (renders $nin: []).
        var rendered = result!.ToString();
        Assert.NotNull(rendered);
    }
```

The second test's final assertion is weak on purpose: the exact node the negation produces (`MongoInExpression { Negated: true }` vs. a `MongoUnaryExpression` wrapper) is decided by the existing negator. Before the change, `TryTranslate` returns `false`, so the `Assert.True` is the real gate. After making it pass, replace the last two lines with an assertion on the concrete node you observe (e.g. `var inExpr = Assert.IsType<MongoInExpression>(result); Assert.True(inExpr.Negated); Assert.Empty((System.Collections.IEnumerable)((MongoConstantExpression)inExpr.Values).Value!);`). Adjust it to the real shape, then keep it.

- [ ] **Step 2: Write failing functional tests.** Create `NativeLocalCollectionContainsTests.cs` (header/BOM from `NativeStringSequenceProjectionTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>Where(e =&gt; localCollection.Contains(e.Member))</c> shapes EF's Northwind aggregate-operator suite exercises
/// beyond a plain List/HashSet/array: set-interface-typed parameters, an empty inline list, a tuple needle, and a
/// closed <c>Enumerable.Where</c> filter applied to the local collection.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeLocalCollectionContainsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int A { get; set; }
        public int B { get; set; }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Name = "a", A = 1, B = 2 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "b", A = 3, B = 4 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "c", A = 1, B = 4 },
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    /// <summary>Runs <paramref name="query"/> natively and against DriverLinq; returns the sorted Names.</summary>
    private List<string> Names(string name, Func<IQueryable<Row>, IQueryable<Row>> query)
    {
        var collection = Seed(name);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return query(db.Entities).Select(r => r.Name).AsEnumerable().Order().ToList();
        });
    }

    [Fact]
    public void IReadOnlySet_parameter_contains_goes_native()
    {
        IReadOnlySet<string> names = new HashSet<string> { "a", "c" };
        Assert.Equal(["a", "c"], Names(nameof(IReadOnlySet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void IImmutableSet_parameter_contains_goes_native()
    {
        IImmutableSet<string> names = ImmutableHashSet<string>.Empty.Add("b");
        Assert.Equal(["b"], Names(nameof(IImmutableSet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void ISet_parameter_contains_goes_native()
    {
        ISet<string> names = new HashSet<string> { "a" };
        Assert.Equal(["a"], Names(nameof(ISet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void Negated_contains_over_empty_inline_list_returns_every_row_natively()
        => Assert.Equal(["a", "b", "c"], Names(nameof(Negated_contains_over_empty_inline_list_returns_every_row_natively),
            q => q.Where(r => !new List<string>().Contains(r.Name))));

    [Fact]
    public void Contains_over_empty_inline_list_returns_no_rows_natively()
        => Assert.Empty(Names(nameof(Contains_over_empty_inline_list_returns_no_rows_natively),
            q => q.Where(r => new List<string>().Contains(r.Name))));
}
```

- [ ] **Step 3: Run to verify failures**

Build, then run `dotnet test $UNIT -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoExpressionTranslatorTests"` and `dotnet test $FUNC -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeLocalCollectionContainsTests"`. The new tests fail: the unit tests on `Assert.True`, the functional tests with `NativeTranslationNotSupportedException`. (`ISet` may already fail the same way.)

- [ ] **Step 4: Implement**

In `TryMatchContainsMethod`, add `using System.Collections.Immutable;` and widen the whitelist (and update the method's `<summary>` list):

```csharp
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>) || def == typeof(ICollection<>)
                    || def == typeof(ISet<>) || def == typeof(IReadOnlySet<>) || def == typeof(IImmutableSet<>))
```

Keep it an explicit whitelist. Do **not** generalize to "any type implementing `IEnumerable<T>`": that would capture `string.Contains(char)`.

In `TranslateInValues`, directly after the `if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)` line:

```csharp
        // `new List<string>()` inline (EF's Contains_with_local_collection_empty_inline): EF's parameter extraction
        // leaves an argument-less collection construction as a bare NewExpression rather than folding it to a
        // constant. It is always empty, so it is an empty $in/$nin haystack.
        if (unwrapped is NewExpression { Arguments.Count: 0 } emptyCollection
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(emptyCollection.Type))
        {
            return new MongoConstantExpression(Array.CreateInstance(elementType, 0), property);
        }
```

- [ ] **Step 5: Run unit + functional tests → pass.** Tighten the empty-list unit assertion as described in Step 1.

- [ ] **Step 6: Spec tests.** For `IReadOnlySet_Contains_with_parameter IImmutableSet_Contains_with_parameter Contains_with_local_collection_empty_inline`: run native-only (they should pass outright, or fail only on `AssertMql`), regenerate if needed, `git diff`, rebuild, and rerun in both modes → green.

- [ ] **Step 7: Regression sweep** (native-only failures −6 vs. the previous task; no new failures). Also run `dotnet test $UNIT -c "Debug EF10" --no-build` in full, because `TryMatchContainsMethod` has four callers (`EntityEquality.cs:288`, `MongoExpressionTranslator.cs:997,1020,2157`, `NativeJoinScopeTranslator.cs:373`).

- [ ] **Step 8: Commit** — `git commit -m "EF-322: native Contains over ISet/IReadOnlySet/IImmutableSet and an empty inline list"`

---

### Task 3: Tuple needle in `Contains`

Closes gap **B3** (1 test).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (the `TryMatchContainsMethod` arm, ~lines 1020–1060)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` (`RenderInValues`, ~line 629)
- Modify: `tests/.../FunctionalTests/Query/NativeLocalCollectionContainsTests.cs` (append)
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Consumes: `NativeLocalCollectionContainsTests.Names(...)` from Task 2. Existing private members of `MongoExpressionTranslator`: `IsTupleType(Type)`, `TryDecomposeTupleOperand(Expression, out MongoExpression[])` (TupleEquality.cs), `static AllFieldsDefaultSerialized(MongoExpression)`, `static Unwrap(Expression)`, `static TranslateInValuesRaw(Expression, Type)`.

- [ ] **Step 1: Failing functional tests** (append to `NativeLocalCollectionContainsTests`):

```csharp
    [Fact]
    public void Tuple_parameter_array_contains_constructed_tuple_goes_native()
    {
        var pairs = new[] { Tuple.Create(1, 2), Tuple.Create(3, 4) };
        Assert.Equal(["a", "b"], Names(nameof(Tuple_parameter_array_contains_constructed_tuple_goes_native),
            q => q.Where(r => pairs.Contains(new Tuple<int, int>(r.A, r.B)))));
    }

    [Fact]
    public void Tuple_contains_is_positional_not_set_membership()
    {
        // (4, 1) must NOT match row c = (A: 1, B: 4): tuple equality is positional.
        var pairs = new[] { Tuple.Create(4, 1) };
        Assert.Empty(Names(nameof(Tuple_contains_is_positional_not_set_membership),
            q => q.Where(r => pairs.Contains(Tuple.Create(r.A, r.B)))));
    }
```

- [ ] **Step 2: Run → both fail** with `NativeTranslationNotSupportedException`.

- [ ] **Step 3: Implement the needle arm.** In `MongoExpressionTranslator.cs`, inside the `case MethodCallExpression call when TryMatchContainsMethod(...)` arm, directly **before** the `if (TryTranslateValue(itemExpr, out var needleNode) ...` computed-needle block:

```csharp
                // A constructed System.Tuple/ValueTuple needle over entity fields — `ids.Contains(new Tuple<int,
                // int>(o.OrderID, o.ProductID))` (EF's Contains_with_local_tuple_array_closure) or the Tuple.Create
                // spelling. The positional sibling of the anonymous-type needle below, rendered as an MQL array exactly
                // like the tuple-equality operand (MongoExpressionTranslator.TupleEquality.cs). Only a query-PARAMETER
                // haystack is admitted: its elements serialize through the driver's own tuple serializer (one BSON
                // array per tuple — see MongoAggregationExpressionRenderer.RenderInValues), whereas a constant haystack
                // would render through BsonValue.Create, which cannot serialize a tuple.
                if (IsTupleType(itemExpr.Type)
                    && TryDecomposeTupleOperand(Unwrap(itemExpr), out var tupleElements)
                    && new MongoTupleExpression(tupleElements) is var tupleNeedle
                    && AllFieldsDefaultSerialized(tupleNeedle)
                    && MongoAggregationExpressionRenderer.CanRender(tupleNeedle))
                {
                    return TranslateInValuesRaw(collectionExpr, itemExpr.Type) is MongoParameterExpression tupleValues
                        ? new MongoComputedInExpression(tupleNeedle, tupleValues, negated: false)
                        : null;
                }
```

- [ ] **Step 4: Serialize tuple haystack elements as arrays.** In `MongoAggregationExpressionRenderer.RenderInValues`, parameter case, replace the `elementSerializer` initializer (add `using System.Runtime.CompilerServices;` and `using MongoDB.Bson.Serialization;` if absent):

```csharp
                var elementSerializer = parameter.ForSerialization is not null
                    ? BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization).Serializer
                    : parameter.RawElementType is { } rawElementType
                        ? typeof(ITuple).IsAssignableFrom(rawElementType)
                            // The driver's own Tuple/ValueTuple serializers write one BSON array per tuple, matching how
                            // a MongoTupleExpression needle renders; BsonSerializerFactory.CreateTypeSerializer would fall
                            // through to a class-map (document) serializer for a tuple type.
                            ? BsonSerializer.LookupSerializer(rawElementType)
                            : BsonSerializerFactory.CreateTypeSerializer(rawElementType)
                        : StringSerializer.Instance;
```

- [ ] **Step 5: Run the functional tests → pass.** If they fail with zero rows, capture the MQL (see the `SpyLoggerProvider` usage in `NativeJoinTests`) and check that the haystack renders as `[[1, 2], [3, 4]]`, not `[{ "Item1": 1, … }]`.

- [ ] **Step 6: Spec test** `Contains_with_local_tuple_array_closure`: native-only → regenerate → both modes green. Expected MQL: `OrderDetails.{ "$match" : { "$expr" : { "$in" : [["$_id.OrderID", "$_id.ProductID"], [[1, 2], [10248, 11]]] } } }` (same as the driver's).

- [ ] **Step 7: Regression sweep** (−2). Also run `dotnet test $UNIT --no-build -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionNodeCoverageTests|FullyQualifiedName~MongoAggregationExpressionRenderer"`.

- [ ] **Step 8: Commit** — `git commit -m "EF-322: native tuple needle in local-collection Contains"`

---

### Task 4: Fold a closed `Enumerable.Where` over a local collection

Closes gap **B4** (2 tests).

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Visitors/LocalCollectionFilterFoldingVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/MongoQueryTranslationPreprocessor.cs` (`Process`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (the preprocessor line in "Pipeline at a glance")
- Modify: `tests/.../FunctionalTests/Query/NativeLocalCollectionContainsTests.cs` (append)
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Consumes: `NativeQueryParameter.TryGetQueryParameterName(Expression, out string?)`; EF's `QueryCompilationContext.RegisterRuntimeParameter(string, LambdaExpression)`, which returns `ParameterExpression` on EF8/EF9 and `QueryParameterExpression` on EF10 (typed here as `Expression`); `QueryCompilationContext.QueryContextParameter`.
- Produces: `internal sealed class LocalCollectionFilterFoldingVisitor(QueryCompilationContext) : ExpressionVisitor`.

Design: EF's funcletizer leaves `Enumerable.Where(<constant or query-parameter collection>, e => e != null)` unevaluated inside a `Contains`. Folding the closed filter on the client turns it into a plain, indexable `$in` on **both** query paths (MQL is not contract, so this is fine):
- constant source: evaluate it once at compile time into a `T[]` constant;
- parameter source: register a runtime parameter whose extractor re-applies the filter to the parameter's value on **every execution** (Review Focus 1).

The fold only happens when the predicate lambda is closed: it references nothing but its own parameter, with no query parameters, extension nodes or nested lambdas.

- [ ] **Step 1: Failing functional tests** (append to `NativeLocalCollectionContainsTests`):

```csharp
    [Fact]
    public void Filtered_inline_list_contains_goes_native()
        => Assert.Equal(["a"], Names(nameof(Filtered_inline_list_contains_goes_native),
            q => q.Where(r => new List<string?> { "a", null, "zzz" }.Where(n => n != null).Contains(r.Name))));

    // Review Focus 1: the same compiled query executed twice with a different captured value.
    [Fact]
    public void Filtered_parameter_collection_is_re_evaluated_per_execution()
    {
        var collection = Seed(nameof(Filtered_parameter_collection_is_re_evaluated_per_execution));
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq })
        {
            var id = "a";
            using (var db = CreateContext(collection, mode))
                Assert.Equal(["a"], db.Entities.Where(r => new List<string?> { "zzz", id }.Where(n => n != null).Contains(r.Name))
                    .Select(r => r.Name).ToList());

            id = "b";
            using (var db = CreateContext(collection, mode))
                Assert.Equal(["b"], db.Entities.Where(r => new List<string?> { "zzz", id }.Where(n => n != null).Contains(r.Name))
                    .Select(r => r.Name).ToList());
        }
    }

    [Fact]
    public void Filter_capturing_another_variable_is_not_folded_and_declines_cleanly()
    {
        var excluded = "a";
        var names = new List<string> { "a", "b" };
        var collection = Seed(nameof(Filter_capturing_another_variable_is_not_folded_and_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Where(r => names.Where(n => n != excluded).Contains(r.Name)).Select(r => r.Name).ToList();
        });
        Assert.Equal(["b"], result);
    }
```

- [ ] **Step 2: Run → the first two fail** (`NativeTranslationNotSupportedException`). The third should already pass. If the third fails because DriverLinq itself can't translate the shape, replace it with a test that only asserts `Assert.Throws<NativeTranslationNotSupportedException>` under `NativeOnly`, and note that in the commit message.

- [ ] **Step 3: Create the visitor** `src/MongoDB.EntityFrameworkCore/Query/Visitors/LocalCollectionFilterFoldingVisitor.cs` (license header copied from `MongoQueryTranslationPreprocessor.cs`, same BOM status):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Folds a CLOSED <c>Enumerable.Where(localCollection, e =&gt; …)</c> that is the collection argument of an
/// <c>Enumerable.Contains</c> — e.g. <c>new List&lt;string&gt; { "ABCDE", id }.Where(e =&gt; e != null).Contains(c.CustomerID)</c>
/// (EF's <c>Contains_with_local_enumerable_inline</c>/<c>_closure_mix</c>) — into a plain local collection, so the
/// Contains translates to an ordinary indexable <c>$in</c> on both the native and driver-LINQ paths.
/// </summary>
/// <remarks>
/// A constant source is filtered once, here, at compile time. A query-parameter source cannot be (its value is
/// per-execution), so it is replaced by an EF runtime parameter (<see cref="QueryCompilationContext.RegisterRuntimeParameter"/>)
/// whose extractor re-applies the filter to the parameter's value on every execution. Only a predicate that
/// references nothing but its own lambda parameter is folded; anything else is left untouched.
/// </remarks>
internal sealed class LocalCollectionFilterFoldingVisitor(QueryCompilationContext queryCompilationContext) : ExpressionVisitor
{
    private static readonly MethodInfo EnumerableWhere = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Where)
                     && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo EnumerableToArray = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!;

    private int _foldedParameterCount;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node is { Method: { IsStatic: true, Name: nameof(Enumerable.Contains) }, Arguments.Count: 2 }
            && node.Method.DeclaringType == typeof(Enumerable)
            && TryFold(node.Arguments[0], out var folded))
        {
            return node.Update(null, [folded, Visit(node.Arguments[1])]);
        }

        return base.VisitMethodCall(node);
    }

    private bool TryFold(Expression collection, out Expression folded)
    {
        folded = collection;

        if (collection is not MethodCallExpression { Method.IsGenericMethod: true } whereCall
            || whereCall.Method.GetGenericMethodDefinition() != EnumerableWhere
            || whereCall.Arguments[1] is not LambdaExpression predicate
            || !FreeVariableFinder.IsClosed(predicate))
        {
            return false;
        }

        var source = whereCall.Arguments[0];
        var toArray = EnumerableToArray.MakeGenericMethod(whereCall.Method.GetGenericArguments()[0]);

        if (source is ConstantExpression)
        {
            var evaluate = Expression.Lambda<Func<object?>>(
                Expression.Convert(Expression.Call(toArray, whereCall), typeof(object)));
            folded = Expression.Constant(evaluate.Compile()(), toArray.ReturnType);
            return true;
        }

        if (NativeQueryParameter.TryGetQueryParameterName(source, out var parameterName))
        {
            var queryContext = QueryCompilationContext.QueryContextParameter;
            var sourceValue = Expression.Convert(
                Expression.Property(ParameterValues(queryContext), "Item", Expression.Constant(parameterName)),
                source.Type);
            var extractor = Expression.Lambda(
                Expression.Call(toArray, Expression.Call(whereCall.Method, sourceValue, predicate)), queryContext);

            // "__" prefix: on EF8/EF9 a query parameter is a ParameterExpression recognized by that prefix
            // (NativeQueryParameter.TryGetQueryParameterName); harmless on EF10.
            folded = queryCompilationContext.RegisterRuntimeParameter(
                $"__mongo_filtered_{_foldedParameterCount++}", extractor);
            return true;
        }

        return false;
    }

    private static Expression ParameterValues(ParameterExpression queryContext)
#if EF8 || EF9
        => Expression.Property(queryContext, nameof(QueryContext.ParameterValues));
#else
        => Expression.Property(queryContext, nameof(QueryContext.Parameters));
#endif

    private sealed class FreeVariableFinder(IReadOnlyCollection<ParameterExpression> bound) : ExpressionVisitor
    {
        private bool _found;

        public static bool IsClosed(LambdaExpression lambda)
        {
            var finder = new FreeVariableFinder(lambda.Parameters);
            finder.Visit(lambda.Body);
            return !finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= !bound.Contains(node);
            return node;
        }

        // A QueryParameterExpression (EF10) or any other provider/EF extension node: not closed.
        protected override Expression VisitExtension(Expression node)
        {
            _found = true;
            return node;
        }

        // Nested lambdas: out of scope, keep the fold narrow.
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            _found = true;
            return node;
        }
    }
}
```

`Expression.Property(dict, "Item", key)` needs the dictionary's indexer. EF10's `Parameters` is `Dictionary<string, object?>` and EF8/9's `ParameterValues` is `IReadOnlyDictionary<string, object?>`; both expose `Item`. If `Expression.Property(…, "Item", …)` fails to resolve on the interface type, use `Expression.MakeIndex(dict, dict.Type.GetProperty("Item"), [Expression.Constant(parameterName)])`, looking the property up on `typeof(IReadOnlyDictionary<string, object?>)` for EF8/9.

- [ ] **Step 4: Wire it into the preprocessor.** In `MongoQueryTranslationPreprocessor.Process`, directly after the `EntityFrameworkDetourExpressionVisitor` line:

```csharp
        query = new LocalCollectionFilterFoldingVisitor(QueryCompilationContext).Visit(query);
```

- [ ] **Step 5: Update `Query/AGENTS.md`.** Change the preprocessor line in "Pipeline at a glance" to:

```
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; fold closed Where over a local Contains collection; lift VectorSearch out before nav expansion)
```

- [ ] **Step 6: Build all three configurations** (the `#if` must compile everywhere):

```bash
for c in "Debug EF8" "Debug EF9" "Debug EF10"; do dotnet build MongoDB.EFCoreProvider.sln -c "$c" 2>&1 | grep -E "Error\(s\)"; done
```

Expected `0 Error(s)` ×3.

- [ ] **Step 7: Run the functional tests (EF10) → pass.**

- [ ] **Step 8: Spec tests** `Contains_with_local_enumerable_inline Contains_with_local_enumerable_inline_closure_mix`: native-only → regenerate → both modes green. Expected MQL becomes an indexable `{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }` (and `"ANATR"` for the second closure-mix query).

- [ ] **Step 9: Regression sweep** (−4). Also run the **normal-mode** spec Query sweep and grep the diff for unexpectedly changed baselines: this rewrite also changes the driver-LINQ path.

- [ ] **Step 10: Commit** — `git commit -m "EF-322: fold closed Enumerable.Where over a local Contains collection"`

---

### Task 5: Reference-type upcast around a DTO projection

Closes gap **C** (1 test, EF9+).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (top of `TryPopulateNativeProjection`, line ~44)
- Create: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderUpcastTests.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeUpcastProjectionTests.cs`
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

- [ ] **Step 1: Failing unit test** (pattern from `NativeProjectionBinderStringSequenceTests`; header/BOM from that file):

```csharp
using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class NativeProjectionBinderUpcastTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    private class IdDto { public string Name { get; set; } = ""; }
    private class IdAndCityDto : IdDto { public string City { get; set; } = ""; }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Customer>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Customer))!);
    }

    [Fact]
    public void Upcast_member_init_projection_binds_the_construction()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, IdDto>> selector = c => (IdDto)new IdAndCityDto { Name = c.Name, City = c.City };
        Assert.Equal(ExpressionType.Convert, selector.Body.NodeType); // guard: the compiler kept the Convert

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "Name");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "City");
    }

    [Fact]
    public void Value_type_conversion_of_a_construction_is_not_unwrapped()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object>> selector = c => (object)new ValueTuple<string>(c.Name);
        // Boxing a struct is not a reference upcast; this test pins that the new arm does not fire for it.
        NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector);
        Assert.DoesNotContain(mongoQ.Select.Projection, p => p.Alias == "Item1");
    }
}
```

If `Assert.Equal(ExpressionType.Convert, …)` fails because the C# compiler elides the upcast in the lambda, build the selector by hand instead: `Expression.Lambda<Func<Customer, IdDto>>(Expression.Convert(memberInit, typeof(IdDto)), c)`.

- [ ] **Step 2: Failing functional test** `NativeUpcastProjectionTests.cs`. Copy the `Row`/`Seed`/`CreateContext` pattern from `NativeSelectorlessAggregateTests`, with `Row { ObjectId Id; string Name; string City }`, seeded ("a","London"), ("b","Berlin"):

```csharp
    public class IdDto { public string Name { get; set; } = ""; }
    public class IdAndCityDto : IdDto { public string City { get; set; } = ""; }

    [Fact]
    public void First_over_covariant_dto_query_goes_native()
    {
        var collection = Seed(nameof(First_over_covariant_dto_query_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            IQueryable<IdDto> query = db.Entities.Where(r => r.Name == "b")
                .Select(r => new IdAndCityDto { Name = r.Name, City = r.City });
            var first = query.First();
            return [(first.Name, ((IdAndCityDto)first).City)];
        });
        Assert.Equal([("b", "Berlin")], result);
    }

    [Fact]
    public void Last_over_covariant_dto_query_goes_native()
    {
        var collection = Seed(nameof(Last_over_covariant_dto_query_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            IQueryable<IdDto> query = db.Entities.OrderBy(r => r.Name)
                .Select(r => new IdAndCityDto { Name = r.Name, City = r.City });
            return [query.Last().Name];
        });
        Assert.Equal(["b"], result);
    }
```

- [ ] **Step 3: Run → fail** (the unit test on `Assert.True`; the functional tests with `NativeTranslationNotSupportedException`).

- [ ] **Step 4: Implement.** At the very top of `NativeProjectionBinder.TryPopulateNativeProjection`, before the existing re-entrancy guard:

```csharp
        // A reference-type upcast wrapped around a constructed DTO — `x => (CustomerIdDto)new CustomerIdAndCityDto
        // { ... }` — which EF's nav-expansion produces when a covariant IQueryable<Derived> is consumed as
        // IQueryable<Base> (EF's Return_type_of_singular_operator_is_preserved). The upcast changes nothing
        // server-side, so bind the construction itself; the Convert stays on the shaper, which TranslateSelect
        // builds from its own (unmodified) selector.
        if (selector.Body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: NewExpression or MemberInitExpression } upcast
            && !upcast.Type.IsValueType
            && !upcast.Operand.Type.IsValueType
            && upcast.Type.IsAssignableFrom(upcast.Operand.Type))
        {
            selector = Expression.Lambda(upcast.Operand, selector.Parameters);
        }
```

- [ ] **Step 5: Run unit + functional → pass.** If the unit test passes but the functional test returns empty or wrong members, then the shaper side (`MongoProjectionBindingExpressionVisitor`) keys the construction's members differently when they sit under a `Convert`. In that case, look at how its `Visit` handles `UnaryExpression Convert` over `MemberInit`, and make it bind the operand's members under the same `ProjectionMember`s the binder aliased. Fix it there, and add a unit test for it next to this one.

- [ ] **Step 6: Spec test** `Return_type_of_singular_operator_is_preserved` (EF10): native-only → regenerate (6 queries; the rewriter truncates at 9) → both modes green.

- [ ] **Step 7: Regression sweep** (−2). **Commit** — `git commit -m "EF-322: native projection through a reference-type upcast of a DTO"`

---

### Task 6: `string.FirstOrDefault()` as a client-applied string leaf

Closes gap **D** (1 test).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (`IsStringSequenceMaterializationCall`, line ~580, and its doc comment)
- Modify: `tests/.../UnitTests/Query/NativeTranslation/NativeProjectionBinderStringSequenceTests.cs` (append)
- Modify: `tests/.../FunctionalTests/Query/NativeStringSequenceProjectionTests.cs` (append)
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

Rationale: the read side of this leaf (`MongoProjectionBindingRemovingExpressionVisitor` ~line 258 and `MongoMixedProjectionBindingRemovingExpressionVisitor.TryBindStringSequenceLeaf`) rebuilds `Expression.Call(call.Method, rawStringRead)`. That works for any single-argument `Enumerable` call on a `string`, so a scalar `char` result needs no reader changes. MongoDB has no char type (the driver serializes `char` as Int32), so pushing down the raw string and applying `FirstOrDefault` on the client is the right native form. Today's driver path does full client evaluation (the baseline pipeline is empty), so this is a strict improvement.

- [ ] **Step 1: Failing unit test** (append to `NativeProjectionBinderStringSequenceTests`):

```csharp
    [Fact]
    public void Bare_FirstOrDefault_over_string_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, char>> selector = c => c.City.FirstOrDefault();

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("City", Assert.IsType<MongoFieldExpression>(projection.Expression).ElementName);
        Assert.True(mongoQ.Select.HasStringSequenceProjectionLeaf);
    }
```

(Add `using System.Linq;` if the file lacks it.)

- [ ] **Step 2: Failing functional test** (append to `NativeStringSequenceProjectionTests`; it uses the file's own `Seed`, `CreateContext` and `AllModes`):

```csharp
    [Fact]
    public void FirstOrDefault_over_string_projection_goes_native_in_every_mode()
    {
        var collection = Seed(nameof(FirstOrDefault_over_string_projection_goes_native_in_every_mode));
        collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Label = "c", City = "" });

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);
            var bare = db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.City.FirstOrDefault()).ToList();
            Assert.Equal(['L', 'B', '\0'], bare);

            var wrapped = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, Initial = x.City.FirstOrDefault() }).ToList();
            Assert.Equal([('a', 'L'), ('b', 'B'), ('c', '\0')], wrapped.Select(r => (r.Label[0], r.Initial)));
        }
    }
```

- [ ] **Step 3: Run → fail** (the unit test on `Assert.True`; the functional test under `NativeOnly`).

- [ ] **Step 4: Implement.** Extend `IsStringSequenceMaterializationCall`:

```csharp
    internal static bool IsStringSequenceMaterializationCall(MethodCallExpression call)
        => call is { Method.IsGenericMethod: true, Arguments: [var source] }
           && source.Type == typeof(string)
           && call.Method.GetGenericMethodDefinition() is var definition
           && (definition == EnumerableMethods.AsEnumerable
               || definition == EnumerableMethods.ToList
               || definition == EnumerableMethods.ToArray
               || definition == EnumerableMethods.FirstOrDefaultWithoutPredicate);
```

Update its `<summary>`: add that `FirstOrDefault()` (EF's `String_FirstOrDefault_in_projection_does_not_do_client_eval`) is admitted for the same reason. It needs only the raw string server-side, and the shaper re-applies the call, producing a scalar `char` rather than a sequence.

- [ ] **Step 5: Run → pass.** Run the whole `NativeStringSequenceProjectionTests` and `NativeProjectionBinderStringSequenceTests` classes: all modes must stay green.

- [ ] **Step 6: Spec test** `String_FirstOrDefault_in_projection_does_not_do_client_eval`: native-only → regenerate. Expected `Customers.{ "$project" : { "_v" : "$_id", "_id" : 0 } }`, or whatever the bare-field projection renders, instead of `Customers.`. Both modes green.

- [ ] **Step 7: Regression sweep** (−2). **Commit** — `git commit -m "EF-322: native string.FirstOrDefault() projection leaf"`

---

### Task 7: Reducer after a row-count-preserving `$lookup`

Closes gap **E** (2 tests).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQueryExpression.Lookup.cs` (new helper)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` (`TryBindReducer`, the `HasConfirmedJoinLookup` guard ~line 67)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (the two "every join 1:1-safe" loops in `IsSingleEligibleNativeJoinScope`, ~lines 1136–1180: a pure refactor onto the helper)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReducerAfterReferenceLookupTests.cs`
- Modify: `tests/.../FunctionalTests/Query/NativeJoinTests.cs` (append one required-navigation guard test)
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Produces: `internal bool MongoQueryExpression.AreAllJoinsRowCountPreserving()`.

Why it is safe: a left-outer join over a reference (non-collection) navigation lowers to `$lookup` + `$unwind(preserveNullAndEmptyArrays: true)`, which neither drops nor multiplies rows. A reducer's `$limit` placed before it is therefore equivalent to one placed after it. That is the same 1:1 argument `IsSingleEligibleNativeJoinScope` already relies on for paging. If a `Where` or `OrderBy` reached the joined side first (`JoinInnerAccessConfirmed`), `ActiveOps` already routes the `$limit` into `PostJoinOps`, after the lookup. And `PostLookupPagingOps` deferral only happens for joins that are *not* 1:1-safe, so the two cannot combine here.

- [ ] **Step 1: Pure refactor first — add the helper and use it in `IsSingleEligibleNativeJoinScope`.** In `MongoQueryExpression.Lookup.cs`:

```csharp
    /// <summary>
    /// Whether every recorded join lowers to a row-count-preserving <c>$lookup</c>/<c>$unwind</c>: a left-outer join
    /// over a reference (non-collection) navigation, whose <c>$unwind</c> (<c>preserveNullAndEmptyArrays: true</c>)
    /// neither drops nor multiplies rows — so a <c>$skip</c>/<c>$limit</c> placed before the join is equivalent to the
    /// same stage placed after it.
    /// </summary>
    internal bool AreAllJoinsRowCountPreserving()
    {
        foreach (var join in Joins)
        {
            if (join.Lookup is not { } lookup || !(join.IsLeftOuter && lookup.Navigation is { IsCollection: false }))
                return false;
        }

        return true;
    }
```

Replace the reducer-branch loop in `IsSingleEligibleNativeJoinScope` with `if (!mongoQueryExpression.AreAllJoinsRowCountPreserving()) return false;`. Replace the paging-branch loop plus the `everyJoinPreLookupSafe` local with `var everyJoinPreLookupSafe = mongoQueryExpression.AreAllJoinsRowCountPreserving();`. Keep all comments.

Build, then run `dotnet test $FUNC -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"` and the spec Query sweep (normal mode). They must be identical to before. Commit on its own: `git commit -m "EF-322: extract MongoQueryExpression.AreAllJoinsRowCountPreserving (refactor)"`.

- [ ] **Step 2: Failing functional tests.** Create `NativeReducerAfterReferenceLookupTests.cs`, modeled on `NativeJoinTests`' context (header/BOM from that file). The navigation is **optional**, a nullable FK, so EF emits a left-outer join:

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
/// First/FirstOrDefault/Single composed after a projection through an OPTIONAL reference navigation
/// (<c>OrderBy(..).Select(p =&gt; p.Customer!.City).First()</c> — EF's Northwind <c>OfType_Select</c>). The join is a
/// left-outer reference <c>$lookup</c>, which preserves the row count, so the reducer's <c>$limit</c> is safe to emit
/// before it.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReducerAfterReferenceLookupTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Customer
    {
        public ObjectId Id { get; set; }
        public string City { get; set; } = "";
    }

    public class Purchase
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private sealed class Ctx(TemporaryDatabaseFixture database, string customers, string purchases, MongoQueryMode mode)
        : DbContext(BuildOptions(database, mode))
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Purchase> Purchases { get; set; } = null!;

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var builder = new DbContextOptionsBuilder<Ctx>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(builder).UseQueryMode(mode);
            return builder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection(customers);
            modelBuilder.Entity<Purchase>(b =>
            {
                b.ToCollection(purchases);
                b.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).IsRequired(false);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>Seq 1 has a NULL FK, Seq 2 a DANGLING FK, Seq 3 a matched FK ("London").</summary>
    private Func<MongoQueryMode, Ctx> Seed(string name)
    {
        var suffix = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var london = new Customer { Id = ObjectId.GenerateNewId(), City = "London" };
        database.MongoDatabase.GetCollection<Customer>("C" + suffix).InsertOne(london);
        database.MongoDatabase.GetCollection<Purchase>("P" + suffix).InsertMany(
        [
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 1, CustomerId = null },
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 2, CustomerId = ObjectId.GenerateNewId() },
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 3, CustomerId = london.Id },
        ]);
        return mode => new Ctx(database, "C" + suffix, "P" + suffix, mode);
    }

    // Review Focus 4: the first row has a null FK — First() must return its (null) City, not skip it.
    [Fact]
    public void First_over_optional_reference_nav_projection_goes_native_and_keeps_null_fk_row()
    {
        var create = Seed(nameof(First_over_optional_reference_nav_projection_goes_native_and_keeps_null_fk_row));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return [db.Purchases.OrderBy(p => p.Seq).Select(p => p.Customer!.City).First()];
        });
        Assert.Equal([null], result);
    }

    [Fact]
    public void FirstOrDefault_after_skip_over_dangling_fk_row_goes_native()
    {
        var create = Seed(nameof(FirstOrDefault_after_skip_over_dangling_fk_row_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return [db.Purchases.OrderBy(p => p.Seq).Skip(1).Select(p => p.Customer!.City).FirstOrDefault()];
        });
        Assert.Equal([null], result);
    }

    [Fact]
    public void Single_over_matched_row_goes_native()
    {
        var create = Seed(nameof(Single_over_matched_row_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return [db.Purchases.Where(p => p.Seq == 3).Select(p => p.Customer!.City).Single()];
        });
        Assert.Equal(["London"], result);
    }

    [Fact]
    public void Last_over_optional_reference_nav_projection_goes_native()
    {
        var create = Seed(nameof(Last_over_optional_reference_nav_projection_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return [db.Purchases.OrderBy(p => p.Seq).Select(p => p.Customer!.City).Last()];
        });
        Assert.Equal(["London"], result);
    }
}
```

And append this guard to `NativeJoinTests`. `Order.Owner` is a **required** navigation (non-nullable FK ⇒ inner join ⇒ not row-count-preserving):

```csharp
    [Fact]
    public void First_over_required_reference_nav_projection_with_dangling_first_row_is_correct_in_every_mode()
    {
        // Order.OwnerId is non-nullable, so EF inner-joins: the dangling first order is DROPPED by the join and
        // First() must answer with the matched order's owner. A $limit emitted before this (non-row-count-preserving)
        // $lookup would keep only the dangling order and return nothing — the reducer must decline (or be correct).
        var seed = SeedDanglingOrderFirstThenMatched();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode, nameof(First_over_required_reference_nav_projection_with_dangling_first_row_is_correct_in_every_mode) + mode);
            Assert.Equal("Alice", db.Orders.OrderBy(o => o.Region).Select(o => o.Owner!.Name).First());
        }
    }
```

Before writing that test, check `SeedDanglingOrderFirstThenMatched` (~line 2395). It must put the dangling order first under `OrderBy(o => o.Region)` and name the matched owner `"Alice"`. If it doesn't, adjust the ordering key or the expected name to match that helper; don't change the helper.

- [ ] **Step 3: Run → the four new `NativeReducerAfterReferenceLookupTests` fail** with `NativeTranslationNotSupportedException`. The `NativeJoinTests` guard passes already.

- [ ] **Step 4: Implement.** In `TryBindReducer` replace

```csharp
        if (select.HasConfirmedJoinLookup)
            return false;
```

with

```csharp
        // ...unless every confirmed join is row-count-preserving (a left-outer REFERENCE-navigation $lookup, e.g.
        // `OrderBy(o => o.OrderID).Select(o => o.Customer.City).First()` — EF's OfType_Select): its $unwind
        // (preserveNullAndEmptyArrays: true) neither drops nor multiplies rows, so a $limit before it is exactly
        // equivalent to one after it — the same 1:1 argument IsSingleEligibleNativeJoinScope already relies on for
        // paging. (If a Where/OrderBy reached the join's Inner side first, ActiveOps already routes the $limit into
        // PostJoinOps, after the lookup, anyway.)
        if (select.HasConfirmedJoinLookup && !mongoQ.AreAllJoinsRowCountPreserving())
            return false;
```

Update the preceding comment's "(EF-392)" paragraph so it says the decline applies to joins that are **not** row-count-preserving.

- [ ] **Step 5: Run → pass.** Also run the whole `NativeJoinTests` class. `First_after_a_confirmed_join_declines_cleanly_under_NativeOnly` (a collection-nav join) must stay green.

- [ ] **Step 6: Spec tests** `OfType_Select OfType_Select_OfType_Select`: native-only. They may already match the existing baselines (the native MQL can be identical to the driver's). Regenerate only if `AssertMql` fails. Both modes green.

- [ ] **Step 7: Regression sweep** (−4). Also run `NorthwindMiscellaneousQueryMongoTest.Projection_take_projection`, `.Projection_skip_projection` and `.Projection_skip_take_projection` in both modes: they depend on the refactored predicate.

- [ ] **Step 8: Commit** — `git commit -m "EF-322: native reducer after a row-count-preserving reference lookup"`

---

### Task 8: `Queryable.Contains` as a terminal operator

Closes gap **F** (3 tests).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` (new `TryBindContains` + private `TryTranslateContainsItem`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateContains`, ~line 2453)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (`IsNativeRepresentableSlotOperator`, ~line 717)
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeContainsTerminalTests.cs`
- Modify (baselines): `NorthwindAggregateOperatorsQueryMongoTest.cs`

**Interfaces:**
- Consumes: `NativeCardinalityBinder.TryGetBareServerValueProjection` (Task 1); `TryBindAggregate(mongoQ, MongoAggregateOperator.Any, selector: null, predicate, typeof(bool))` (existing Any+predicate arm); `MongoExpressionTranslator.Unwrap` (internal static); `NativeQueryParameter.TryGetQueryParameterName`. Entity equality against a constant or query-parameter entity is `MongoExpressionTranslator.EntityEquality.cs`; `e == null` is `TranslateComparisonCore`'s `$$ROOT`-vs-null arm.
- Produces: `internal static bool NativeCardinalityBinder.TryBindContains(MongoQueryExpression mongoQ, Expression sourceShaper, Expression item)`.

Semantics: `source.Contains(item)` ≡ `source.Any(x => x == item)`.
- **Scalar source** (`Select(c => c.CustomerID).Contains("ALFKI")`): the bare projection must be a plain `MongoFieldExpression` on a query with **no joins** (a joined field would be matched before its `$lookup`). The conjunct is `field == item`, appended to `PipelineOps` *after* any recorded paging, so `Take(1).Contains(v)` tests only the paged rows.
- **Entity source** (`Where(...).Contains(entity)` / `.Contains(null)`): `Any(e => e == item)` through the existing entity-equality translation. Excludes a client-wrapped whole-entity shaper.
- Anything else declines.

- [ ] **Step 1: Failing functional tests.** Create `NativeContainsTerminalTests.cs`, reusing the `Row`/`Seed`/`CreateContext` pattern from `NativeSelectorlessAggregateTests`, with `Row { ObjectId Id; string Name; int Value }` seeded ("a",1), ("b",2), ("c",3):

```csharp
    private List<bool> Run(string name, Func<SingleEntityDbContext<Row>, bool> query)
    {
        var collection = Seed(name);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return [query(db)];
        });
    }

    [Fact]
    public void Scalar_select_contains_constant_goes_native()
    {
        Assert.Equal([true], Run(nameof(Scalar_select_contains_constant_goes_native) + "T", db => db.Entities.Select(r => r.Name).Contains("b")));
        Assert.Equal([false], Run(nameof(Scalar_select_contains_constant_goes_native) + "F", db => db.Entities.Select(r => r.Name).Contains("zzz")));
    }

    [Fact]
    public void Scalar_select_contains_parameter_goes_native()
    {
        var wanted = "c";
        Assert.Equal([true], Run(nameof(Scalar_select_contains_parameter_goes_native), db => db.Entities.Select(r => r.Name).Contains(wanted)));
    }

    // Review Focus 3.
    [Fact]
    public void Take_before_Contains_only_tests_the_paged_rows()
        => Assert.Equal([false], Run(nameof(Take_before_Contains_only_tests_the_paged_rows),
            db => db.Entities.OrderBy(r => r.Value).Select(r => r.Name).Take(1).Contains("b")));

    [Fact]
    public void Entity_contains_goes_native()
    {
        var collection = Seed(nameof(Entity_contains_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            var b = db.Entities.Single(r => r.Name == "b");
            return [db.Entities.Where(r => r.Value >= 2).Contains(b), db.Entities.Where(r => r.Value >= 3).Contains(b)];
        });
        Assert.Equal([true, false], result);
    }

    [Fact]
    public void Entity_contains_null_is_false_natively()
        => Assert.Equal([false], Run(nameof(Entity_contains_null_is_false_natively), db => db.Entities.Where(r => r.Value > 0).Contains(null!)));

    // Review Focus 5 — a computed projection has no single backing field to match against: decline, don't guess.
    [Fact]
    public void Contains_over_computed_projection_declines_cleanly()
    {
        var collection = Seed(nameof(Contains_over_computed_projection_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return [db.Entities.Select(r => r.Name + "!").Contains("b!")];
        });
        Assert.Equal([true], result);
    }
```

If `Contains_over_computed_projection_declines_cleanly` can't run because DriverLinq itself fails for the shape, replace it with an `Assert.Throws<NativeTranslationNotSupportedException>` under `NativeOnly` only, and note that in the commit message.

- [ ] **Step 2: Run → the native tests fail** with `NativeTranslationNotSupportedException` ("Query projects a non-entity result"). The decline test may already pass.

- [ ] **Step 3: Implement `TryBindContains`** in `NativeCardinalityBinder` (add `using System.Diagnostics.CodeAnalysis;` and `using Microsoft.EntityFrameworkCore.Metadata;` if absent):

```csharp
    /// <summary>
    /// Binds a terminal <c>source.Contains(item)</c> as the equivalent <c>source.Any(x =&gt; x == item)</c>:
    /// over a bare scalar-field <c>Select</c> (<c>Select(c =&gt; c.CustomerID).Contains("ALFKI")</c>) as an equality
    /// <c>$match</c> on that field, or over a whole-entity source (<c>Where(...).Contains(order)</c>,
    /// <c>.Contains(null)</c>) via the ordinary entity-equality translation. Returns <see langword="false"/> for any
    /// other shape (a computed or joined projection, a client-wrapped shaper), so the caller marks the query non-native.
    /// </summary>
    internal static bool TryBindContains(MongoQueryExpression mongoQ, Expression sourceShaper, Expression item)
    {
        var select = mongoQ.Select;

        if (TryGetBareServerValueProjection(mongoQ, sourceShaper) is { } bareSource)
        {
            // Joins.Count == 0: a joined field (`_lookup_X.City`) would be matched in PipelineOps, BEFORE its $lookup.
            if (mongoQ.Joins.Count != 0
                || bareSource.Expression is not MongoFieldExpression field
                || !TryTranslateContainsItem(item, field.Property, out var itemNode))
            {
                return false;
            }

            if (!TryBindAggregate(mongoQ, MongoAggregateOperator.Any, selector: null, predicate: null, typeof(bool)))
                return false;

            // Committed only after TryBindAggregate succeeded (no mutate-then-decline). AddPredicateConjunct appends
            // at the TAIL of the op list, so a Take/Skip recorded earlier still runs first.
            select.AddPredicateConjunct(new MongoBinaryExpression(MongoBinaryOperator.Equal, field, itemNode));
            return true;
        }

        var elementType = sourceShaper.Type;
        if (select.Route != NativeRoute.WholeEntity
            || select.HasClientWrappedWholeEntityShaper
            || !mongoQ.CollectionExpression.EntityType.ClrType.IsAssignableFrom(elementType)
            || !elementType.IsAssignableFrom(item.Type))
        {
            return false;
        }

        var element = Expression.Parameter(elementType, "e");
        var comparand = item.Type == elementType ? item : Expression.Convert(item, elementType);
        var predicate = Expression.Lambda(Expression.Equal(element, comparand), element);
        return TryBindAggregate(mongoQ, MongoAggregateOperator.Any, selector: null, predicate, typeof(bool));
    }

    private static bool TryTranslateContainsItem(
        Expression item, IProperty property, [NotNullWhen(true)] out MongoExpression? node)
    {
        var unwrapped = MongoExpressionTranslator.Unwrap(item);
        node = unwrapped switch
        {
            ConstantExpression constant => new MongoConstantExpression(constant.Value, property),
            _ when NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var name)
                => new MongoParameterExpression(name, property),
            _ => null
        };
        return node != null;
    }
```

If `Expression.Equal(element, comparand)` throws for an entity type without an `==` operator, it won't: reference types fall back to reference equality in expression trees. If the entity type is a struct it would, but EF entity types can't be structs.

- [ ] **Step 4: Wire it up.** In the QMTEV:

```csharp
    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeCardinalityBinder.TryBindContains(mongoQ, source.ShaperExpression, item))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return ReshapeShaperExpression(source, typeof(bool));
    }
```

In `NativeSlotPopulator.IsNativeRepresentableSlotOperator` add `|| methodDefinition == QueryableMethods.Contains` next to `AnyWithoutPredicate`. Both edits are required: `Query/AGENTS.md` says "a `Translate*`-override operator needs both the override and `IsNativeRepresentableSlotOperator`".

- [ ] **Step 5: Run → pass.** Then run `NativeCardinalityTests`, `NativeEntityEqualityTests` and `NativeGateRoutingTests` → green.

- [ ] **Step 6: Spec tests** `Contains_top_level Contains_over_entityType_should_rewrite_to_identity_equality Contains_over_entityType_with_null_should_rewrite_to_false`: native-only → regenerate → both modes green. Expected shapes: `Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$limit" : 1 }` for `Contains_top_level`, and a `$$ROOT`-vs-null `$expr` match for the null case. Accept whatever the renderer produces, as long as the results are correct.

- [ ] **Step 7: Regression sweep** (−6). The target class must now be **0 failures** under `MONGODB_EF_NATIVE_ONLY=1` on EF10.

- [ ] **Step 8: Commit** — `git commit -m "EF-322: native Queryable.Contains terminal operator"`

---

### Task 9: Cross-version verification and review

**Files:** baselines only, if EF8/EF9 differ.

- [ ] **Step 1: Full suites on all three versions.** Invoke the `/test-all` skill (builds and tests EF8, EF9 and EF10 in parallel). Expected: no new failures vs. `main`-equivalent baselines. Any EF8/EF9-only failure in the touched tests gets fixed in the owning task's code, or with an EF-version `#if` baseline in the spec file, following the existing `#if` pattern in `NorthwindAggregateOperatorsQueryMongoTest.cs`.

- [ ] **Step 2: Native-only on EF8 and EF9 for the target class.**

```bash
for c in "Debug EF8" "Debug EF9"; do
  MONGODB_EF_NATIVE_ONLY=1 dotnet test $SPEC -c "$c" --no-build --filter "FullyQualifiedName~NorthwindAggregateOperatorsQueryMongoTest" | tail -2
done
```

Expected `Failed: 0` for both, except `Return_type_of_singular_operator_is_preserved`, which doesn't exist on EF8. EF8 hands inline arrays over as `NewArrayExpression`, so if Task 2's or Task 4's shapes differ there, fix them in the relevant recognizer (e.g. `TranslateInValues` already handles `NewArrayInit`), not by skipping.

- [ ] **Step 3: Whole-suite native-only diff (EF10).** Re-run the Task 0 script against a fresh native-only Query trx and diff against `baseline-native-failures.txt`. The only differences allowed are the 48 fixed entries (plus any other classes' tests that went native as a side effect; list them in the PR description).

- [ ] **Step 4: Branch review.** Invoke `/review-ef-core-provider`. It runs `query-reviewer`, `ef-conformance-reviewer`, `api-stability-reviewer` and the rest over the branch diff. Address its findings, re-run Steps 1–3, and commit fixes as `EF-322: address review findings`.
