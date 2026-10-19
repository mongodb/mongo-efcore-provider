# Native GroupBy SP4: Computed Select Projection Over Key/Accumulators Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate a Select-projection member whose value is a COMPUTED expression tree (a ternary or a
null-coalesce) combining a `g.Key`/`g.Key.Sub` leaf with a constant — moving 2 `NorthwindGroupByQueryMongoTest`
methods off the driver-LINQ fallback: `GroupBy_aggregate_projecting_conditional_expression_based_on_group_key`,
`GroupBy_orderby_projection_with_coalesce_operation`.

**Architecture:** `NativeGroupByBinder.TryBindGroupProjection`'s flatten loop currently requires each Select
member's value to be EXACTLY a key access (`TryGetKeyMemberPath`) or EXACTLY an accumulator call
(`TryBindAccumulator`) — a computed expression combining one with a constant falls through both and declines.
This plan adds a THIRD attempt, tried after both existing ones fail: a new recursive function,
`TryTranslateGroupProjectionExpression`, that walks a `ConditionalExpression` (ternary) or `Coalesce`
`BinaryExpression` (`??`), recursing into each branch/operand through the SAME function, with two leaf cases —
a `g.Key`/`g.Key.Sub` access (resolved to `MongoElementRefExpression("_id"[.Sub])`, the group's own POST-`$group`
output — this runs in the FLATTENING `$project` stage, unlike SP3's accumulator-condition resolution which had
to avoid `"_id"` because it ran INSIDE `$group` itself) and a key-vs-constant comparison (the ternary's own
`Test`, e.g. `g.Key == null`). Anything that does NOT reference the grouping parameter delegates to the
existing `MongoExpressionTranslator.TryTranslateValue` (handles constants, casts, arithmetic — the ordinary
value translator). Both target IR node types this needs — `MongoConditionalExpression` and
`MongoCoalesceExpression` — already exist in `src/MongoDB.EntityFrameworkCore/Query/Expressions/` (used
elsewhere by the ordinary, non-GroupBy value translator); this plan adds zero new node types.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$project`, `$cond`, `$ifNull`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP4 section)

## Global Constraints

- **`Native == DriverLinq` invariant** — every target test's `AssertQuery`/`AssertMql` must keep passing under
  `MongoQueryMode.Native` exactly as it does today (via fallback), in addition to newly passing under
  `MongoQueryMode.NativeOnly`.
- **No new `MongoExpression` subtype** — reuse the already-existing `MongoConditionalExpression` and
  `MongoCoalesceExpression`. Do not touch `MongoExpressionNodeCoverageTests.cs`; nothing there needs a new row.
- **A predicate/leaf that references the grouping parameter in a shape this plan doesn't recognize must
  DECLINE, never silently mis-resolve** — same discipline SP3 established (`ReferencesParameter`, already in
  `NativeGroupByBinder.cs`, reusable here): a member access that happens to share a name with a real property
  on the entity type must not be resolved against the WRONG type just because the ordinary translator doesn't
  know it's looking at the wrong parameter.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.

## Review Focus

- **A computed leaf referencing an ACCUMULATOR (e.g. `g.Key == null ? g.Count() : 0`) must DECLINE, not
  silently bind wrong.** Neither target test needs an accumulator inside a computed projection leaf (both only
  combine a key access with a plain constant) — this plan deliberately does not implement it. Task 1's own test
  proves the decline, not a crash or partial translation.
- **A composite key's WHOLE-key comparison to `null` (`g.Key == null` where the key is `new { A, B }`)** —
  `TryGetKeyMemberPath`'s `allowWholeKeyRead: true` (the flatten loop's own default) resolves even a composite
  key's bare `g.Key` to `"_id"` wholesale; comparing a composite sub-document to a scalar `null` is structurally
  fine to emit but the shape has no realistic query meaning. Task 1 leaves this admitted (matching
  `TryGetKeyMemberPath`'s existing, already-tested behavior) rather than special-casing a decline nobody asked
  for — Task 1's own test only pins the SCALAR-key case its two targets actually need.
- **A predicate/leaf combining a per-element reference with a key reference in the SAME condition** (e.g.
  `o.Amount > 0 ? g.Key : "none"` where the ternary's TEST reads a per-element field, not the key) — Task 1
  must decline this cleanly via `ReferencesParameter`'s existing guard, not attempt a mixed resolution.
- **A chained coalesce (`a ?? b ?? c`)** — `BinaryExpression`'s own `Coalesce` shape is left-associative
  (`a ?? (b ?? c)`), so the recursive function must recurse into the RIGHT operand when it is itself a
  `Coalesce`, not just the immediate two operands. Task 2's own test pins this, since neither target test's
  single coalesce (`x.Key ?? "Unknown"`) exercises a chain on its own.
- **The already-existing ordering chain (`OrderByDescending(x => x.Count()).ThenBy(x => x.Key)`) ahead of
  `GroupBy_orderby_projection_with_coalesce_operation`'s Select must keep working unmodified** — this plan
  does not touch `PendingGroupOrderings` handling at all; Task 2's own functional/spec verification confirms
  the pre-existing ordering mechanism and the NEW coalesce-projection mechanism compose correctly together,
  not just each in isolation.

---

### Task 1: Ternary (`MongoConditionalExpression`) over a key-vs-constant comparison

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` — add
  `TryTranslateGroupProjectionExpression`; wire it into `TryBindGroupProjection`'s flatten loop (currently
  around line 294–309)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: `TryGetKeyMemberPath(Expression, ParameterExpression, IReadOnlyList<MongoGroupingKeyPart>, bool,
  out string?, bool allowWholeKeyRead = true) : bool` (existing, line ~342), `TryTranslateComparisonConstant
  (Expression, IProperty?, out MongoExpression?) : bool` (existing, line ~1215), `MapComparisonOperator
  (ExpressionType) : MongoBinaryOperator` (existing, line ~1236), `FlipComparison(ExpressionType) :
  ExpressionType` (existing, line ~1249), `ReferencesParameter(Expression, ParameterExpression) : bool`
  (existing, line ~647 — SP3's mixed-reference guard), `MongoExpressionTranslator.TryTranslateValue
  (Expression, out MongoExpression?) : bool` (existing, public).
- Produces: `NativeGroupByBinder.TryTranslateGroupProjectionExpression(Expression expr, ParameterExpression
  groupingParameter, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite, MongoExpressionTranslator
  translator, out MongoExpression? result) : bool` — private, but Task 2 (Coalesce) extends this SAME method
  (adds a new dispatch arm inside it), and `TryBindGroupProjection`'s flatten loop calls it as a third attempt.

- [ ] **Step 1: Write the failing tests**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`. First,
add a nullable key field to the shared `Order` test entity so a `g.Key == null` comparison is meaningful — find
the `private class Order` declaration (search for `public DateTime? ShippedDate`, added in the SP3 fix pass)
and confirm it already has one; if not, add it right after `OrderDate`:

```csharp
        public DateTime? ShippedDate { get; set; }
```

Now find `Sum_with_computed_selector_binds_computed_operand` (the last of SP3's own tests) and add these four
tests immediately after it:

```csharp
    [Fact]
    public void Ternary_over_nullable_key_comparison_binds_conditional_projection()
    {
        // GroupBy_aggregate_projecting_conditional_expression_based_on_group_key's exact shape:
        // .GroupBy(o => o.OrderDate).Select(g => new { Key = g.Key == null ? "is null" : "is not null", ... }).
        // Uses ShippedDate here (nullable, like the real Northwind OrderDate: DateTime?) so the comparison is
        // meaningful.
        var mongoQ = TestQuery();
        Expression<Func<Order, DateTime?>> key = x => x.ShippedDate;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<DateTime?, Order>, object>> proj =
            g => new { Label = g.Key == null ? "is null" : "is not null", Sum = g.Sum(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Two flatten projections: the computed Label and the ordinary Sum accumulator. Flatten output
        // aliases live on MongoSelectDefinition's own Projection list (set via AddProjection), not on
        // MongoGrouping itself — see this file's other tests' own `mongoQ.Select.Projection` usage.
        Assert.Equal(2, mongoQ.Select.Projection.Count);
        var labelProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Label");
        var cond = Assert.IsType<MongoConditionalExpression>(labelProjection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Null(Assert.IsType<MongoConstantExpression>(test.Right).Value);
        Assert.Equal("is null", Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal("is not null", Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);

        // The Sum accumulator itself still binds normally — this plan doesn't touch that path.
        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Ternary_leaf_referencing_an_accumulator_declines()
    {
        // Review Focus: neither target test needs an accumulator inside a computed projection leaf — must
        // decline cleanly, not silently bind wrong (ReferencesParameter's existing guard catches g.Count()
        // the same way it already catches a mixed g.Key reference).
        var mongoQ = TestQuery();
        Expression<Func<Order, DateTime?>> key = x => x.ShippedDate;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<DateTime?, Order>, object>> proj =
            g => new { Label = g.Key == null ? g.Count() : 0 };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Ternary_test_mixing_element_and_key_reference_declines()
    {
        // Review Focus: a per-element reference in the ternary's TEST (not the key) must decline via the
        // SAME ReferencesParameter guard SP3 established, not attempt a mixed resolution.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Label = g.Where(o => o.Amount > 0).Any() ? g.Key : "none" };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Ternary_over_composite_key_null_comparison_admits_whole_key_read()
    {
        // Review Focus: TryGetKeyMemberPath's allowWholeKeyRead:true (already-tested, pre-existing behavior)
        // resolves even a composite key's bare g.Key to "_id" wholesale. Comparing a composite sub-document to
        // a scalar null has no realistic query meaning, but this plan doesn't special-case a decline nobody
        // asked for — it stays structurally admitted, matching the flatten loop's own existing default. Built
        // by hand (not a typed `g => g.Key == null ? ... : ...` lambda) because the composite key's CLR type
        // is a compiler-generated anonymous type with no literal spelling available here; uses the bare-body
        // path (no `new {}` wrapper) the SAME way `TryBindGroupProjection`'s own `isBareBody`/
        // `SyntheticBareProjectionAlias` handling already supports elsewhere in this file.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var groupParam = Expression.Parameter(typeof(IGrouping<object, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<object, Order>.Key));
        var nullConstant = Expression.Constant(null, typeof(object));
        var ternary = Expression.Condition(
            Expression.Equal(keyAccess, nullConstant), Expression.Constant("no key"), Expression.Constant("has key"));
        var proj = Expression.Lambda<Func<IGrouping<object, Order>, object>>(ternary, groupParam);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        var projection = mongoQ.Select.Projection.Single(p => p.Alias == bareAlias);
        var cond = Assert.IsType<MongoConditionalExpression>(projection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: `Ternary_over_nullable_key_comparison_binds_conditional_projection` and
`Ternary_over_composite_key_null_comparison_admits_whole_key_read` FAIL with `Assert.True() Failure`
(`TryBindGroupProjection` currently returns `false` for a computed ternary member — neither
`TryGetKeyMemberPath` nor `TryBindAccumulator` recognizes it). `Ternary_leaf_referencing_an_accumulator_declines`
and `Ternary_test_mixing_element_and_key_reference_declines` PASS already (the shape already declines today,
for the OLD blanket reason — they're here to catch a regression once the new code exists, not to be red now).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, add a new private method
anywhere after `TryGetKeyMemberPath` (e.g. immediately before `TryBindAccumulator`, currently at line 397):

```csharp
    // EF-322 SP4: translates a Select-projection member's value as a COMPUTED expression tree — a ternary or
    // (Task 2) a null-coalesce — whose leaves resolve to a g.Key/g.Key.Sub access or an ordinary translatable
    // value (constant, entity member, arithmetic — anything NOT referencing the grouping parameter). Runs in
    // the FLATTENING $project stage, AFTER $group has already produced "_id" — unlike SP3's
    // TryTranslateAccumulatorCondition (which runs INSIDE $group and must resolve a key reference to its own
    // raw per-input-document expression instead, to avoid a circular reference to $group's own not-yet-
    // computed output), a key leaf here correctly resolves via TryGetKeyMemberPath's ordinary "_id"[.Sub]
    // path, the SAME resolution the flatten loop's own bare-key-member arm already uses.
    private static bool TryTranslateGroupProjectionExpression(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        // A bare g.Key / g.Key.Sub leaf.
        if (TryGetKeyMemberPath(expr, groupingParameter, keyParts, isComposite, out var keyPath))
        {
            if (keyPath == null)
                return false; // bare g.Key over a zero-part key — no single field to read

            result = new MongoElementRefExpression(keyPath, expr.Type);
            return true;
        }

        // A ternary (test ? ifTrue : ifFalse) — every part recurses through this SAME method, so a branch
        // may itself be a nested conditional, a coalesce (Task 2), a key access, or an ordinary value.
        if (expr is ConditionalExpression conditional)
        {
            if (!TryTranslateGroupProjectionConditionOrValue(
                    conditional.Test, groupingParameter, keyParts, isComposite, translator, out var test)
                || !TryTranslateGroupProjectionExpression(
                    conditional.IfTrue, groupingParameter, keyParts, isComposite, translator, out var ifTrue)
                || !TryTranslateGroupProjectionExpression(
                    conditional.IfFalse, groupingParameter, keyParts, isComposite, translator, out var ifFalse))
                return false;

            result = new MongoConditionalExpression(test, ifTrue, ifFalse);
            return true;
        }

        // Not a key access or a ternary — an ordinary expression (constant, entity member, arithmetic) that
        // must NOT reference the grouping parameter in any shape (an accumulator call, a mixed per-element
        // reference, etc.) — see this plan's own Review Focus. Declines rather than letting the ordinary
        // translator, which knows nothing about `g`, mis-resolve a same-named member against the wrong type
        // (the exact bug class SP3's final review found and fixed for its own accumulator conditions).
        if (ReferencesParameter(expr, groupingParameter))
            return false;

        return translator.TryTranslateValue(expr, out result);
    }

    // The BOOLEAN test of a ternary (e.g. `g.Key == null`) — a key-vs-constant comparison, recognized the
    // SAME way SP3's TryTranslateAccumulatorCondition recognizes one, but resolving the key side via
    // TryGetKeyMemberPath's ordinary post-$group "_id"[.Sub] path (this runs in $project, not inside $group —
    // see TryTranslateGroupProjectionExpression's own remarks on why that distinction matters here).
    private static bool TryTranslateGroupProjectionConditionOrValue(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (Unwrap(expr) is BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            if (TryGetKeyMemberPath(bin.Left, groupingParameter, keyParts, isComposite, out var leftPath)
                && leftPath != null
                && TryTranslateComparisonConstant(bin.Right, null, out var rightConst))
            {
                result = new MongoBinaryExpression(
                    MapComparisonOperator(bin.NodeType), new MongoElementRefExpression(leftPath, Unwrap(bin.Left).Type), rightConst);
                return true;
            }

            if (TryGetKeyMemberPath(bin.Right, groupingParameter, keyParts, isComposite, out var rightPath)
                && rightPath != null
                && TryTranslateComparisonConstant(bin.Left, null, out var leftConst))
            {
                result = new MongoBinaryExpression(
                    MapComparisonOperator(FlipComparison(bin.NodeType)), new MongoElementRefExpression(rightPath, Unwrap(bin.Right).Type), leftConst);
                return true;
            }
        }

        // Not a key comparison — delegate to the SAME leaf/ternary/coalesce dispatch as an ordinary value.
        return TryTranslateGroupProjectionExpression(expr, groupingParameter, keyParts, isComposite, translator, out result);
    }
```

Now wire it into `TryBindGroupProjection`'s flatten loop. Find the loop (currently lines 294–309):

```csharp
        foreach (var (memberName, valueExpr) in bindings)
        {
            if (TryGetKeyMemberPath(valueExpr, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite key cannot flatten to a single field

                flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(keyPath, Unwrap(valueExpr).Type)));
                continue;
            }

            if (!TryBindAccumulator(valueExpr, memberName, groupingParameter, keyParts, isComposite, translator, out var acc, out var flattenRead))
                return false;
            accumulators.Add(acc);
            flatten.Add(new MongoProjection(memberName, flattenRead));
        }
```

Replace the final `if (!TryBindAccumulator...) return false;` block with a THIRD attempt before giving up:

```csharp
            if (TryBindAccumulator(valueExpr, memberName, groupingParameter, keyParts, isComposite, translator, out var acc, out var flattenRead))
            {
                accumulators.Add(acc);
                flatten.Add(new MongoProjection(memberName, flattenRead));
                continue;
            }

            // EF-322 SP4: a COMPUTED member value (a ternary or, from Task 2, a coalesce) combining a
            // g.Key/g.Key.Sub leaf with a constant — neither a bare key access nor a bare accumulator call.
            if (!TryTranslateGroupProjectionExpression(valueExpr, groupingParameter, keyParts, isComposite, translator, out var computed))
                return false;

            flatten.Add(new MongoProjection(memberName, computed));
        }
```

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests PASS, including every pre-existing test and the four new ones from Step 1.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: translate a ternary GroupBy projection member over a key-vs-constant comparison"
```

---

### Task 2: Coalesce (`MongoCoalesceExpression`) over a key leaf

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` —
  `TryTranslateGroupProjectionExpression` (Task 1's own new method)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: Task 1's `TryTranslateGroupProjectionExpression` (extends its own body with a new dispatch arm —
  no signature change) and `MongoCoalesceExpression(MongoExpression left, MongoExpression right)` (existing
  IR type, `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCoalesceExpression.cs`).
- Produces: nothing new for later tasks — Task 3 is verification only.

- [ ] **Step 1: Write the failing tests**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`, add these
two tests immediately after Task 1's `Ternary_over_composite_key_null_comparison_admits_whole_key_read`:

```csharp
    [Fact]
    public void Coalesce_over_key_binds_coalesce_projection()
    {
        // GroupBy_orderby_projection_with_coalesce_operation's exact shape:
        // .GroupBy(c => c.City).Select(x => new { Locality = x.Key ?? "Unknown", Count = x.Count() }).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Locality = g.Key ?? "Unknown", Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var localityProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Locality");
        var coalesce = Assert.IsType<MongoCoalesceExpression>(localityProjection.Expression);
        var keyRef = Assert.IsType<MongoElementRefExpression>(coalesce.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Equal("Unknown", Assert.IsType<MongoConstantExpression>(coalesce.Right).Value);

        // The Count accumulator itself still binds normally.
        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Chained_coalesce_over_key_binds_right_nested_coalesce()
    {
        // Review Focus: a ?? b ?? c is left-associative (a ?? (b ?? c)) — the recursive function must walk
        // into the RIGHT operand when it is itself a Coalesce, not just handle two flat operands.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Locality = g.Key ?? "Fallback1" ?? "Fallback2" };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var localityProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Locality");
        var outer = Assert.IsType<MongoCoalesceExpression>(localityProjection.Expression);
        Assert.IsType<MongoElementRefExpression>(outer.Left);
        var inner = Assert.IsType<MongoCoalesceExpression>(outer.Right);
        Assert.Equal("Fallback1", Assert.IsType<MongoConstantExpression>(inner.Left).Value);
        Assert.Equal("Fallback2", Assert.IsType<MongoConstantExpression>(inner.Right).Value);
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: both new tests FAIL with `Assert.True() Failure` — `TryTranslateGroupProjectionExpression` has no
`Coalesce` dispatch arm yet, so a `g.Key ?? "Unknown"` member falls all the way through to the final
`ReferencesParameter`/`translator.TryTranslateValue` arm, which declines (the whole expression references
`groupingParameter` via its `g.Key` sub-expression, so `ReferencesParameter` returns `true` and the method
returns `false`).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, add a `Coalesce` dispatch
arm to `TryTranslateGroupProjectionExpression`, right after the `ConditionalExpression` arm Task 1 added:

```csharp
        // A null-coalescing operator (`left ?? right`) — both operands recurse through this SAME method, so
        // the right operand may itself be a nested Coalesce (a ?? b ?? c is left-associative: a ?? (b ?? c),
        // matching BinaryExpression's own shape) — see this plan's own Review Focus.
        if (expr is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            if (!TryTranslateGroupProjectionExpression(coalesce.Left, groupingParameter, keyParts, isComposite, translator, out var left)
                || !TryTranslateGroupProjectionExpression(coalesce.Right, groupingParameter, keyParts, isComposite, translator, out var right))
                return false;

            result = new MongoCoalesceExpression(left, right);
            return true;
        }
```

Place it immediately after the `if (expr is ConditionalExpression conditional) { ... }` block and before the
final `ReferencesParameter`/`translator.TryTranslateValue` fallback.

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests PASS, including every pre-existing test and the two new ones from Step 1.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: translate a coalesce GroupBy projection member over a key leaf"
```

---

### Task 3: Verify the target spec-test methods, add functional coverage, run every suite

**Files:**
- Test (read-only unless a diff is found): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
  NorthwindGroupByQueryMongoTest.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` — add functional
  coverage for the ternary and coalesce projection shapes

**Interfaces:**
- Consumes: Task 1 + Task 2's production changes — this task is pure verification plus new test coverage.

- [ ] **Step 1: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

- [ ] **Step 2: Run the 2 target methods under default (`Native`) mode; regenerate baselines that changed**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_aggregate_projecting_conditional_expression_based_on_group_key|FullyQualifiedName~GroupBy_orderby_projection_with_coalesce_operation)" \
  --logger "console;verbosity=normal" > /tmp/sp4-native.log 2>&1
tail -60 /tmp/sp4-native.log
```

For any method whose `AssertMql` fails with `Assert.Equal() Failure: Strings differ` (a baseline mismatch, NOT
a data/behavior failure — the native pipeline is very unlikely to match the driver-LINQ fallback's own baseline
MQL shape byte-for-byte), regenerate:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm each diff shows a `$cond`/`$ifNull` shape consistent with this plan's design (the flattening `$project`
computing the Label/Locality member directly from `"$_id"`, not from a `_elements`/`$map`/`$filter` fallback
shape), then rebuild and rerun WITHOUT the env var to confirm green.

- [ ] **Step 3: Run the same 2 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_aggregate_projecting_conditional_expression_based_on_group_key|FullyQualifiedName~GroupBy_orderby_projection_with_coalesce_operation)" \
  --logger "console;verbosity=normal" > /tmp/sp4-nativeonly.log 2>&1
tail -20 /tmp/sp4-nativeonly.log
```

Expected: both methods (4 with async) now PASS under `NativeOnly`.

- [ ] **Step 4: Re-measure the whole class's `NativeOnly` failure count, checking CONTENT not just the number**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp4-full-nativeonly.log 2>&1
tail -8 /tmp/sp4-full-nativeonly.log
grep -E "^\s*Failed MongoDB" /tmp/sp4-full-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: failure count drops from 34 (SP3's ending count) to 30 (34 − 4), and the method-NAME list is exactly
SP3's 17 remaining methods minus these 2 (i.e. 15 names): `GroupBy_Dto_as_key_Select_Sum`,
`GroupBy_count_filter`, `GroupBy_empty_key_Aggregate_Key`, `GroupBy_multi_navigation_members_Aggregate`,
`GroupBy_required_navigation_member_Aggregate`, `GroupBy_selecting_grouping_key_list`,
`GroupBy_skip_0_take_0_aggregate`, `GroupBy_with_group_key_access_thru_navigation`, `Join_GroupBy_Aggregate`,
`Join_groupby_anonymous_orderby_anonymous_projection`, `LongCount_after_GroupBy_aggregate`,
`MinMax_after_GroupBy_aggregate`, `Odata_groupby_empty_key`, `Self_join_GroupBy_Aggregate`,
`Union_simple_groupby`. Do not just check the NUMBER — SP3's own final review found a hidden bonus fix; diff
the actual name list against this expected 15 before proceeding. If the list differs, investigate whether a
name's absence is a genuine bonus fix (a baseline-only fix, confirm via `AssertMql` mismatch not a data/behavior
failure) or a sign this plan's own change touched something unintended, before assuming either way.

- [ ] **Step 5: Add functional coverage for the two new projection shapes**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`. Find its LAST test method
(search for the final `[Fact]` before the file's closing `}`) and add these two tests immediately after it,
before the closing brace:

```csharp
    [Fact]
    public void GroupBy_ternary_projection_over_key_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "D");

        (string Label, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Label = g.Key == "US" ? "domestic" : "international", Total = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .OrderBy(r => r.Label)
                .Select(r => (r.Label, r.Total)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200=300 (domestic); UK 50+25=75 and FR 300 (both international, combined 375).
        Assert.Equal([("domestic", 300m), ("international", 375m)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Label = g.Key == "US" ? "domestic" : "international", Total = g.Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Label)
            .Select(r => (r.Label, r.Total)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_coalesce_projection_over_key_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "D");

        (string Locality, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Locality = g.Key ?? "Unknown", Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Locality)
                .Select(r => (r.Locality, r.Count)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: FR has 1 row, UK has 2, US has 2 — Country is never null in this fixture, so the
        // coalesce's fallback branch is never actually taken, but the SHAPE is still exercised and proven
        // equivalent to the driver-LINQ fallback.
        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Locality = g.Key ?? "Unknown", Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Locality)
            .Select(r => (r.Locality, r.Count)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }
```

- [ ] **Step 6: Run the new functional tests, then the whole Functional suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests" --logger "console;verbosity=normal" \
  > /tmp/sp4-functional-groupby.log 2>&1
tail -10 /tmp/sp4-functional-groupby.log
```

Expected: PASS, 0 failed, including the 2 new tests. If `GroupBy_ternary_projection_over_key_matches_driver_
linq`'s expected tuples don't match — `SeedOrders()`'s actual amounts/countries may differ from what's assumed
above; read the file's own `SeedOrders()` definition and recompute the expected values from the REAL seed data
rather than adjusting the assertion to whatever the run produces.

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp4-functional-full.log 2>&1
tail -8 /tmp/sp4-functional-full.log
```

Expected: PASS, 0 failed (the FULL suite, not just `NativeGroupByTests`).

- [ ] **Step 7: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp4-spec-full.log 2>&1
tail -8 /tmp/sp4-spec-full.log
```

Expected: PASS, 0 failed. If anything OUTSIDE the 2 target methods fails, investigate whether it's a genuine
regression or another "secretly-already-fixed, baseline-only" bonus (SP1's `GroupBy_Property_Select_Sum`,
SP2's `Select_GroupBy_All`, and SP3's `GroupBy_Property_Select_Count/LongCount_with_predicate` +
`Contains_inside_aggregate_function_with_GroupBy` were all this pattern) before assuming either way.

- [ ] **Step 8: Run all three suites on EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" -v quiet
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" -v quiet

for CFG in EF8 EF9; do
  echo "=== $CFG unit ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug $CFG" --no-build 2>&1 | tail -5
  echo "=== $CFG spec (GroupBy class) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -5
  echo "=== $CFG functional (GroupBy) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all three commands.

- [ ] **Step 9: Commit any baseline changes from Step 2, plus the new functional tests**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-322: verify SP4 computed-projection slice — baselines, functional coverage, full suites"
```

---

## After this plan

This plan covers SP4 only (2 tests). SP5–SP9 each get their own plan when their turn comes, using this slice's
post-landing `NativeOnly` failure count (30, confirmed in Task 3 Step 4) as their new baseline. Before starting
SP5's plan, re-verify SP5's own target tests' shapes against the actual EF Core base-test source the same way
this plan's own investigation (and SP2's own correction) did — don't assume the design doc's original bucketing
is exact without a quick re-check.
