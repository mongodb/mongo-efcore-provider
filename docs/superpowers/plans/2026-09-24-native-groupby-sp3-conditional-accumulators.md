# Native GroupBy SP3: Conditional/Filtered Accumulators Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate a per-element FILTERED `$group` accumulator natively — `g.Count(pred)`, `g.Where(pred).Sum/
Min/Max/Average(...)`, and an extra `Where`/`Distinct` hop before `g.Select(...).Distinct().<Op>()` — moving 4
`NorthwindGroupByQueryMongoTest` methods off the driver-LINQ fallback: `GroupBy_multiple_Count_with_predicate`,
`GroupBy_constant_with_where_on_grouping_with_aggregate_operators`, `GroupBy_group_Distinct_Select_Distinct_
aggregate`, `GroupBy_group_Where_Select_Distinct_aggregate`.

**Architecture:** Every filtered shape reduces to the SAME MongoDB idiom: `{"$<op>": {"$cond": [predicate,
operand, "$$REMOVE"]}}` (Count uses a literal `0` for its "else", matching the driver-LINQ fallback's own
existing baseline exactly). `"$$REMOVE"`, empirically verified against a real server (see "Verified design
decision" below), makes `Min`/`Max`/`Sum`/`Average`/`$addToSet`/`$push` treat a non-matching element as though
it contributed nothing at all — critically different from a `null`/`0` sentinel, which would corrupt `Min`/
`Max` (BSON comparison order places `null` below every number/date). No new IR node type is needed: MongoDB's
`"$$REMOVE"` system variable is just a string starting with `"$$"`, and `MongoElementRefExpression` already
renders any `Path` as `"$" + Path` (the SAME mechanism that already renders `"$ROOT"` as `"$$ROOT"` for
`MongoElementRefExpression.WholeRootDocumentPath` — see that constant's own remarks). This plan adds a sibling
constant, `RemoveSentinelPath`, reusing the fully-tested existing node type with zero new dispatcher/coverage
work.

**Verified design decision:** confirmed directly against a real `mongod` (not assumed from documentation):
```
db.t.aggregate([{ $group: { _id: "$g",
    minV: { $min: { $cond: ["$ok", "$v", "$$REMOVE"] } },
    added: { $addToSet: { $cond: ["$ok", "$v", "$$REMOVE"] } }
}}])
```
over `{g:1,v:5,ok:true}`, `{g:1,v:100,ok:false}`, `{g:1,v:1,ok:true}` produces `minV: 1` (never sees `100`) and
`added: [5, 1]` (exactly 2 entries, not 3 with a null placeholder) — `"$$REMOVE"` genuinely removes the array
entry / accumulator contribution, it does not insert a `null`/missing placeholder value.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$cond`, `$$REMOVE`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP3 section)

## Global Constraints

- **`Native == DriverLinq` invariant** — every target test's `AssertQuery`/`AssertMql` must keep passing under
  `MongoQueryMode.Native` exactly as it does today (via fallback), in addition to newly passing under
  `MongoQueryMode.NativeOnly`.
- **Support BOTH the `Queryable` and `Enumerable` forms of `Where`/`Distinct`/`Count`**, matching this file's
  existing dual-form pattern for Count/LongCount (see `TryBindAccumulator`'s own comment: "EF Core lowers a
  grouped aggregate to the Queryable form... a hand-authored Enumerable form is accepted too, used by the unit
  tests"). A hand-written unit test lambda like `g.Where(pred).Min(...)` compiles to the `Enumerable` form
  (`IGrouping<TKey,TElement>` is not `IQueryable`), so checking only `QueryableMethods.Where`/`Distinct` would
  make every new unit test in this plan fail to bind, not just decline gracefully.
- **No new `MongoExpression` subtype** — reuse `MongoElementRefExpression` for the `"$$REMOVE"` sentinel (see
  Architecture above). Do not touch `MongoExpressionNodeCoverageTests.cs`; nothing there needs a new row.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.

## Review Focus

- **A predicate mixing a per-element reference AND `g.Key` in the SAME condition** (e.g. `e => e.OrderID > 5 &&
  e.Key == "x"`, not required by any of this slice's 4 target tests) must DECLINE, not silently translate only
  half of it or crash. Task 1's own helper (`TryTranslateAccumulatorCondition`) only recognizes a predicate that
  is EITHER purely a `g.Key` comparison OR purely an ordinary per-element expression — Task 1 adds a test
  proving the mixed case declines cleanly.
- **`GroupBy_multiple_Count_with_predicate` needs TWO independently-predicated `Count`s in the SAME `Select`**
  (`TenK`/`EleventK`, different thresholds) — Task 1's test proves both bind to DIFFERENT accumulator output
  fields with DIFFERENT conditions, not the same one twice.
- **A key comparison inside an accumulator condition must read the key's RAW per-input-document expression,
  NEVER `"_id"`.** `"_id"` is the `$group` stage's OUTPUT, computed FROM each input document — referencing it
  from within that SAME stage's accumulator input expression is a circular reference MongoDB cannot evaluate.
  This is DIFFERENT from SP2's HAVING key comparison (which runs in a separate, later `$match` stage where
  `"_id"` genuinely exists) — Task 1's test asserts the resolved reference is the key's OWN stored `FieldRef`
  (e.g. a `MongoConstantExpression`), never a `MongoElementRefExpression("_id")`.
- **`g.Distinct()` immediately before `.Select(selector).Distinct()` is a provable no-op for ANY subsequent
  reduction** (`{distinct(select(dedupe(S), f))} == {distinct(select(S, f))}` for any set `S` and function
  `f`) — Task 2 must not require translating any predicate for this hop (there is none to translate), only
  verify its OWN source is the grouping parameter directly, and its test should assert the resulting
  accumulator carries NO condition at all (not a trivially-true one) — simpler and consistent with why the
  hop is safe to admit unconditionally.
- **`GroupBy_group_Where_Select_Distinct_aggregate`'s `Max` over `OrderDate.HasValue`-filtered dates going
  through `$$REMOVE`+`$addToSet`+external `$max` reduce (not a direct `$max` accumulator)** must still produce
  the SAME answer as the direct-accumulator case (Task 1) — Task 2 needs its own differential test, not an
  assumption that Task 1's mechanism generalizes automatically to the `$addToSet`-then-external-reduce path.

---

### Task 1: Filtered `Count`/`Sum`/`Min`/`Max`/`Average` via `$cond` + `$$REMOVE`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElementRefExpression.cs` — add
  `RemoveSentinelPath`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` —
  `IsCanonicalCountWithPredicate` (currently line 194) visibility: `private` → `internal`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` — `TryBindAccumulator`
  (currently lines 397–491)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslate(Expression, out MongoExpression?)` (existing, public — the
  ordinary predicate translator, same one `Where` uses), `MongoExpressionTranslator.TryTranslateValue` (existing,
  used for the Sum/Min/Max/Average selector), `MongoGroupingKeyPart` (existing —
  `record MongoGroupingKeyPart(string? Name, MongoExpression FieldRef)`).
- Produces:
  - `MongoElementRefExpression.RemoveSentinelPath` — `internal const string` = `"$REMOVE"`.
  - `NativeGroupByBinder.TryTranslateAccumulatorCondition(Expression predicateBody, ParameterExpression
    groupingParameter, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite, MongoExpressionTranslator
    translator, out MongoExpression? result) : bool` — private, but Task 2 calls it too (same file).
  - `TryBindAccumulator`'s new recognized shapes: `g.Count(pred)`/`g.LongCount(pred)`, and
    `g.Where(pred).Sum/Min/Max/Average(selector)`.

- [ ] **Step 1: Write the failing tests**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs`
and add this test anywhere in the file (a sanity check on the rendering mechanism itself, before it's used
inside a bigger tree):

```csharp
[Fact]
public void Remove_sentinel_renders_as_the_REMOVE_system_variable()
{
    var node = new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, typeof(object));

    var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

    Assert.Equal("$$REMOVE", rendered.AsString);
}
```

Now open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`. Find
`Sum_with_member_selector_binds_sum_operand` (search for it) and add these five tests immediately after it:

```csharp
    [Fact]
    public void Count_with_predicate_binds_conditional_sum()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { TenK = g.Count(e => e.Amount < 100) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("TenK", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);
    }

    [Fact]
    public void Two_counts_with_different_predicates_bind_two_independent_accumulators()
    {
        // GroupBy_multiple_Count_with_predicate's exact shape: TWO differently-thresholded Count(pred) calls
        // in the SAME Select must bind to DIFFERENT output fields with DIFFERENT conditions, not collapse
        // into one.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { TenK = g.Count(e => e.Amount < 100), EleventK = g.Count(e => e.Amount < 200) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        var first = mongoQ.Select.Grouping.Accumulators[0];
        var second = mongoQ.Select.Grouping.Accumulators[1];
        Assert.Equal("TenK", first.OutputField);
        Assert.Equal("EleventK", second.OutputField);
        var firstBound = Assert.IsType<MongoBinaryExpression>(Assert.IsType<MongoConditionalExpression>(first.Operand).Test);
        var secondBound = Assert.IsType<MongoBinaryExpression>(Assert.IsType<MongoConditionalExpression>(second.Operand).Test);
        Assert.Equal(100, Assert.IsType<MongoConstantExpression>(firstBound.Right).Value);
        Assert.Equal(200, Assert.IsType<MongoConstantExpression>(secondBound.Right).Value);
    }

    [Fact]
    public void Where_then_Sum_binds_conditional_operand_with_REMOVE_else()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Where(e => e.Amount > 0).Sum(e => e.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        Assert.Equal("Amount", Assert.IsType<MongoFieldExpression>(cond.IfTrue).ElementName);
        var elseRef = Assert.IsType<MongoElementRefExpression>(cond.IfFalse);
        Assert.Equal(MongoElementRefExpression.RemoveSentinelPath, elseRef.Path);
    }

    [Fact]
    public void Where_on_group_key_then_Min_resolves_key_via_its_own_raw_expression_not_id()
    {
        // GroupBy_constant_with_where_on_grouping_with_aggregate_operators's exact shape: the predicate
        // references g.Key, not the per-element parameter at all. Must resolve to the key's OWN stored
        // FieldRef (here a literal MongoConstantExpression, since the key is GroupBy(o => 1)) — NEVER
        // MongoElementRefExpression("_id"), which does not exist yet inside this accumulator's own $group
        // stage (referencing "_id" here would be a circular reference to that stage's own output).
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> constantKey = x => 1;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, constantKey));

        Expression<Func<IGrouping<int, Order>, object>> proj =
            g => new { Min = g.Where(i => 1 == g.Key).Min(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$min", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        // The key side resolves to the key's own raw expression (a literal 1), not "_id".
        Assert.IsType<MongoConstantExpression>(test.Left);
        Assert.IsNotType<MongoElementRefExpression>(test.Left);
    }

    [Fact]
    public void Where_predicate_mixing_element_and_key_reference_declines()
    {
        // Deliberately out of scope: TryTranslateAccumulatorCondition only recognizes a predicate that is
        // EITHER purely a g.Key comparison OR purely an ordinary per-element expression, never both combined
        // in one condition. Must decline cleanly (fall back), not crash or silently drop half the condition.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Where(e => e.Amount > 5 && e.Key == "ALFKI").Sum(e => e.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }
```

- [ ] **Step 2: Run the test files to confirm the new tests fail against today's code**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests|FullyQualifiedName~Remove_sentinel_renders_as_the_REMOVE_system_variable"
```

Expected: `Remove_sentinel_renders_as_the_REMOVE_system_variable` FAILS to even COMPILE (`RemoveSentinelPath`
doesn't exist yet — a compile error is the correct RED here, same as every other "add a not-yet-existing
member" step in this repo's plans). `Count_with_predicate_binds_conditional_sum`,
`Two_counts_with_different_predicates_bind_two_independent_accumulators`,
`Where_then_Sum_binds_conditional_operand_with_REMOVE_else`, and
`Where_on_group_key_then_Min_resolves_key_via_its_own_raw_expression_not_id` FAIL with `Assert.True() Failure`
(currently `TryBindGroupProjection` returns `false` for all of these — none of these shapes bind yet).
`Where_predicate_mixing_element_and_key_reference_declines` PASSES already (the shape already declines today,
for the OLD reason — it's here to catch a regression once the new code exists, not to be red now).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElementRefExpression.cs`, add a new constant right
after `WholeRootDocumentPath`'s declaration:

```csharp
    /// <summary>
    /// The <see cref="Path"/> spelling that means "treat this element's contribution as though it were
    /// entirely absent", i.e. the aggregation system variable <c>$$REMOVE</c> — rendered as <c>"$" +
    /// "$REMOVE"</c> by the ordinary <c>"$" + Path</c> rule, exactly like <see cref="WholeRootDocumentPath"/>
    /// above. Verified directly against a real server (not assumed from documentation): inside a $group
    /// accumulator's own input expression, `{"$cond": [pred, value, "$$REMOVE"]}` makes Min/Max/Sum/Average/
    /// $push/$addToSet skip that element entirely — critically different from a null/0 sentinel, which would
    /// corrupt Min/Max (BSON comparison order places null below every number/date, so a null "else" would
    /// silently become the reported minimum whenever any element failed the predicate).
    /// </summary>
    internal const string RemoveSentinelPath = "$REMOVE";
```

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`, change
line 194's `private static bool IsCanonicalCountWithPredicate` to `internal static bool
IsCanonicalCountWithPredicate` (no other change to that method).

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, add two new private helper
methods anywhere after `TryBindAccumulator` (e.g. immediately before `TryBindDistinctAccumulator`, currently at
line 503):

```csharp
    // EF-322 SP3: resolves g.Key / g.Key.Sub, inside an ACCUMULATOR's own condition/operand, to the key
    // part's OWN raw per-input-document expression (the SAME expression already used to compute _id's value)
    // — NEVER "_id" itself. An accumulator's condition/operand is evaluated PER INPUT DOCUMENT, inside the
    // SAME $group stage that computes _id as its OUTPUT — referencing "_id" here would be a circular
    // reference the server cannot evaluate. Contrast with NativeGroupByBinder's SP2 HAVING key comparison
    // (TryBindGroupSideOperand), which correctly DOES use "_id"[.path] — that one runs in a separate, LATER
    // $match stage, after $group has already produced _id.
    private static bool TryResolveKeyReferenceAsRawExpression(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            if (isComposite)
                return false; // whole composite key has no single raw expression to compare against a scalar

            result = keyParts[0].FieldRef;
            return true;
        }

        if (member.Expression is MemberExpression { Member.Name: "Key" } inner && inner.Expression == groupingParameter)
        {
            foreach (var part in keyParts)
            {
                if (part.Name == member.Member.Name)
                {
                    result = part.FieldRef;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Translates a per-element accumulator CONDITION (from <c>g.Count(pred)</c>, <c>g.Where(pred).Op(...)</c>,
    /// or the leading <c>Where</c>/<c>Distinct</c> hop <see cref="TryBindDistinctAccumulator"/> also uses).
    /// Recognizes exactly two shapes — a comparison whose one side is a <c>g.Key</c>/<c>g.Key.Sub</c> access
    /// (resolved via <see cref="TryResolveKeyReferenceAsRawExpression"/>, since every element in a group
    /// shares the SAME key value, this is a valid per-element condition even though it never actually varies
    /// per element), or an ORDINARY per-element expression (delegated to <paramref name="translator"/>'s
    /// normal <c>TryTranslate</c>, the same predicate translator <c>Where</c> itself uses). Deliberately does
    /// NOT handle a predicate that COMBINES both in one condition (e.g. <c>e => e.Amount > 5 &amp;&amp;
    /// e.Key == "x"</c>) — no target shape needs it; declines so the whole query falls back rather than
    /// silently translating only half the condition.
    /// </summary>
    private static bool TryTranslateAccumulatorCondition(
        Expression predicateBody,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (Unwrap(predicateBody) is BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            if (TryResolveKeyReferenceAsRawExpression(bin.Left, groupingParameter, keyParts, isComposite, out var leftKey)
                && TryTranslateComparisonConstant(bin.Right, null, out var rightConst))
            {
                result = new MongoBinaryExpression(MapComparisonOperator(bin.NodeType), leftKey, rightConst);
                return true;
            }

            if (TryResolveKeyReferenceAsRawExpression(bin.Right, groupingParameter, keyParts, isComposite, out var rightKey)
                && TryTranslateComparisonConstant(bin.Left, null, out var leftConst))
            {
                result = new MongoBinaryExpression(MapComparisonOperator(FlipComparison(bin.NodeType)), rightKey, leftConst);
                return true;
            }
        }

        // Not a g.Key comparison — an ordinary per-element predicate (e.g. e.Amount < 100,
        // e.OrderDate.HasValue). The ordinary translator resolves members against the entity type directly,
        // regardless of which lambda parameter name the predicate happens to use.
        return translator.TryTranslate(predicateBody, out result);
    }
```

Now, inside `TryBindAccumulator` (currently lines 397–491), add the `Count(predicate)` recognition right after
the EXISTING parameterless-Count block (currently lines 444–456, ending `return true; }` right before the
`// Sum / Average / Min / Max with a selector` comment):

```csharp
        // EF-322 SP3: g.Count(pred) / g.LongCount(pred) — a per-element PREDICATED count, reducing to
        // $sum: {$cond: [translatedPredicate, 1, 0]} (0, not $$REMOVE, matching the driver-LINQ fallback's
        // own existing baseline exactly — a non-matching element contributes 0 either way for a sum of 1s).
        if (call.Arguments.Count == 2 && IsCanonicalCountWithPredicate(call.Method)
            && call.Arguments[1].UnwrapLambdaFromQuote() is { } countPred
            && TryTranslateAccumulatorCondition(countPred.Body, groupingParameter, keyParts, isComposite, translator, out var countCond))
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum",
                new MongoConditionalExpression(countCond, new MongoConstantExpression(1, null), new MongoConstantExpression(0, null)));
            flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
            return true;
        }
```

`TryBindAccumulator`'s signature must grow two parameters — `IReadOnlyList<MongoGroupingKeyPart> keyParts` and
`bool isComposite` — since the new code above needs them. Change the signature (currently lines 397–403):

```csharp
    private static bool TryBindAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
```

This changes 4 existing call sites in this same file — update all of them to pass `keyParts, isComposite`
(both are already local variables in scope at every call site):
1. `TryBindGroupProjection`'s ordering-accumulator loop: `TryBindAccumulator(body, syntheticField, groupParam,
   translator, out var acc, out var flattenRead)` → add `keyParts, isComposite` after `groupParam`.
2. `TryBindGroupProjection`'s main flatten loop: `TryBindAccumulator(valueExpr, memberName, groupingParameter,
   translator, out var acc, out var flattenRead)` → same.
3. `TryBindGroupSideOperand` (SP2's HAVING accumulator match): `TryBindAccumulator(side, accumulatorOutputField,
   groupingParameter, translator, out var acc, out _)` → same (it already has `keyParts`/`isComposite` as its
   own parameters).
4. `TryBindDistinctAccumulator`'s own internal call is NOT affected — Task 2 rewrites that method's signature
   separately, but Task 1 should NOT touch `TryBindDistinctAccumulator` yet (leave it uncalled by the new
   parameters for now — Task 2 owns wiring it).

Add the equivalent parameters to `TryBindDistinctAccumulator`'s and `TryBindElementSelectedAccumulator`'s OWN
signatures too, purely for pass-through (neither needs the values YET — Task 2 is the one that uses them in
`TryBindDistinctAccumulator`'s body) — this keeps `TryBindAccumulator`'s two calls to them
(`TryBindDistinctAccumulator(call, outputField, groupingParameter, translator, out accumulator, out
flattenRead)` and `TryBindElementSelectedAccumulator(call, outputField, groupingParameter, translator, out
accumulator, out flattenRead)`, both inside `TryBindAccumulator`'s own body) compiling without a signature
mismatch:

```csharp
    private static bool TryBindDistinctAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
```

```csharp
    private static bool TryBindElementSelectedAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
```

(Neither method's BODY changes in this task — just the signature, so `TryBindAccumulator`'s two call sites to
them compile with the extra arguments. Task 2 is the one that actually uses `keyParts`/`isComposite` inside
`TryBindDistinctAccumulator`'s body.)

Finally, add the `Where(pred).Op(selector)` recognition. This is a NEW accumulator shape whose call SOURCE
(`call.Arguments[0]`) is `g.Where(pred)`, not `g` directly — like `TryBindDistinctAccumulator`/
`TryBindElementSelectedAccumulator`, it must be tried BEFORE the generic `IsGroupingSource(call.Arguments[0],
...)` guard (currently line 439) would otherwise reject it. Add this new private method right after
`TryBindElementSelectedAccumulator`'s closing brace (before `IsGroupingSource`, currently at line 623):

```csharp
    /// <summary>
    /// EF-322 SP3: <c>g.Where(pred).Sum/Min/Max/Average(selector)</c> — a per-element FILTERED aggregate,
    /// reducing to <c>{"$&lt;op&gt;": {"$cond": [translatedPred, translatedOperand, "$$REMOVE"]}}</c>.
    /// <c>"$$REMOVE"</c> (empirically verified — see this plan's own "Verified design decision") makes
    /// Min/Max/Sum/Average treat a non-matching element as though it contributed nothing at all.
    /// </summary>
    private static bool TryBindFilteredAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (call.Arguments.Count != 2)
            return false;

        var definition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;
        string? op = EnumerableMethods.IsSumWithSelector(call.Method) || QueryableMethods.IsSumWithSelector(call.Method) ? "$sum"
            : EnumerableMethods.IsAverageWithSelector(call.Method) || QueryableMethods.IsAverageWithSelector(call.Method) ? "$avg"
            : EnumerableMethods.IsMinWithSelector(call.Method) || definition == QueryableMethods.MinWithSelector ? "$min"
            : EnumerableMethods.IsMaxWithSelector(call.Method) || definition == QueryableMethods.MaxWithSelector ? "$max"
            : null;

        if (op is null)
            return false;

        // The source must be g.Where(pred) — a Where call whose OWN source is the grouping parameter
        // directly. Both the Queryable and Enumerable forms are accepted — see this plan's own Global
        // Constraints note on why (a hand-written unit test lambda produces the Enumerable form).
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } whereCall
            || (whereCall.Method.GetGenericMethodDefinition() != QueryableMethods.Where
                && whereCall.Method.GetGenericMethodDefinition() != EnumerableMethods.Where)
            || whereCall.Arguments.Count != 2
            || !IsGroupingSource(whereCall.Arguments[0], groupingParameter))
            return false;

        if (whereCall.Arguments[1].UnwrapLambdaFromQuote() is not { } wherePred
            || !TryTranslateAccumulatorCondition(wherePred.Body, groupingParameter, keyParts, isComposite, translator, out var condition))
            return false;

        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, op,
            new MongoConditionalExpression(condition, operand,
                new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, operand.Type)));
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }
```

Wire it into `TryBindAccumulator`'s dispatch, in the SAME "try before the generic guard" position as the other
two special-shape binders (currently lines 426–437, right after the `TryBindElementSelectedAccumulator` call):

```csharp
        if (TryBindElementSelectedAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        if (TryBindFilteredAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;
```

- [ ] **Step 4: Run the test files again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests|FullyQualifiedName~MongoAggregationExpressionRendererTests"
```

Expected: all tests in both files PASS, including every pre-existing test and the six new ones from Step 1.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed — `TryBindAccumulator`'s signature change touches every call site in this file;
confirm nothing else depended on the old 6-parameter form.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElementRefExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs
git commit -m "EF-322: translate filtered GroupBy accumulators (Count(pred), Where(pred).Op()) via \$cond+\$\$REMOVE"
```

---

### Task 2: Extra `Where`/`Distinct` hop before `g.Select(...).Distinct().<Op>()`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` —
  `TryBindDistinctAccumulator` (currently lines 503–559)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: Task 1's `TryTranslateAccumulatorCondition(Expression, ParameterExpression,
  IReadOnlyList<MongoGroupingKeyPart>, bool, MongoExpressionTranslator, out MongoExpression?) : bool` and
  `MongoElementRefExpression.RemoveSentinelPath`.
- Produces: `TryBindDistinctAccumulator`'s widened source recognition — no new public/internal surface other
  callers need to know about (its signature already grew `keyParts`/`isComposite` in Task 1, unused there
  until now).

- [ ] **Step 1: Write the failing tests**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`, find
`Sum_with_member_selector_binds_sum_operand`'s section (the same area Task 1 added tests to) and add these two
tests:

```csharp
    [Fact]
    public void Distinct_then_Select_Distinct_Max_treats_leading_Distinct_hop_as_a_no_op()
    {
        // GroupBy_group_Distinct_Select_Distinct_aggregate's exact shape: g.Distinct() immediately before
        // .Select(selector).Distinct() is a provable no-op for ANY subsequent reduction (deduping whole rows
        // first can only ever match or exceed the final distinct-mapped set's size, never change it) — must
        // bind with NO condition at all, not a trivially-true one.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Max = g.Distinct().Select(e => e.OrderDate).Distinct().Max() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$addToSet", acc.Operator);
        // No $cond wrapper — the operand is the bare field reference, unconditional.
        Assert.IsType<MongoFieldExpression>(acc.Operand);
    }

    [Fact]
    public void Where_then_Select_Distinct_Max_binds_conditional_addToSet_with_REMOVE_else()
    {
        // GroupBy_group_Where_Select_Distinct_aggregate's exact shape: a genuine per-element filter before
        // Select(...).Distinct().Max() — this one DOES need a $cond, unlike the Distinct-hop case above.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Max = g.Where(e => e.OrderDate.HasValue).Select(e => e.OrderDate).Distinct().Max() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$addToSet", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.Equal("OrderDate", Assert.IsType<MongoFieldExpression>(cond.IfTrue).ElementName);
        var elseRef = Assert.IsType<MongoElementRefExpression>(cond.IfFalse);
        Assert.Equal(MongoElementRefExpression.RemoveSentinelPath, elseRef.Path);
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: `Distinct_then_Select_Distinct_Max_treats_leading_Distinct_hop_as_a_no_op` and
`Where_then_Select_Distinct_Max_binds_conditional_addToSet_with_REMOVE_else` FAIL (`TryBindDistinctAccumulator`
currently requires the Select's source to be EXACTLY `g`, via `IsGroupingSource` — an extra `Distinct()`/
`Where(...)` hop in front of it is rejected outright today).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, add a new private helper
right before `TryBindDistinctAccumulator` (currently line 503):

```csharp
    // EF-322 SP3: resolves the OPTIONAL leading hop before g.Select(selector).Distinct().<Op>() — either
    // NOTHING (source is g directly), a bare g.Distinct() (a provable no-op for this shape — see this plan's
    // own Review Focus note — admitted with NO condition, not a trivially-true one), or a genuine g.Where(pred)
    // (a real per-element filter, admitted WITH a translated condition). Both Queryable and Enumerable forms
    // of Where/Distinct are accepted, same reasoning as TryBindFilteredAccumulator.
    private static bool TryResolveOptionalAccumulatorSourceCondition(
        Expression source,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        out MongoExpression? condition)
    {
        condition = null;
        source = Unwrap(source);

        if (IsGroupingSource(source, groupingParameter))
            return true; // bare g — no condition

        if (source is not MethodCallExpression { Method.IsGenericMethod: true } call)
            return false;

        var definition = call.Method.GetGenericMethodDefinition();

        if ((definition == QueryableMethods.Distinct || definition == EnumerableMethods.Distinct)
            && call.Arguments.Count == 1
            && IsGroupingSource(call.Arguments[0], groupingParameter))
        {
            return true; // g.Distinct() before Select(...).Distinct() — no condition needed
        }

        if ((definition == QueryableMethods.Where || definition == EnumerableMethods.Where)
            && call.Arguments.Count == 2
            && IsGroupingSource(call.Arguments[0], groupingParameter)
            && call.Arguments[1].UnwrapLambdaFromQuote() is { } pred)
        {
            return TryTranslateAccumulatorCondition(pred.Body, groupingParameter, keyParts, isComposite, translator, out condition);
        }

        return false;
    }
```

Replace the source-chain check inside `TryBindDistinctAccumulator` (currently lines 534–545):

```csharp
        // The source must be g.Select(selector).Distinct() — a Distinct() call whose OWN source is a Select
        // over the grouping parameter (never g directly, which is the ordinary-accumulator shape above).
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } distinctCall
            || distinctCall.Method.GetGenericMethodDefinition() != QueryableMethods.Distinct
            || distinctCall.Arguments.Count != 1)
            return false;

        if (Unwrap(distinctCall.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } selectCall
            || selectCall.Method.GetGenericMethodDefinition() != QueryableMethods.Select
            || selectCall.Arguments.Count != 2
            || !IsGroupingSource(selectCall.Arguments[0], groupingParameter))
            return false;
```

with:

```csharp
        // The source must be g.Select(selector).Distinct() — a Distinct() call whose OWN source is a Select.
        // The Select's OWN source is either g directly, or ONE extra hop (g.Where(pred) or g.Distinct()) —
        // EF-322 SP3, see TryResolveOptionalAccumulatorSourceCondition's own remarks.
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } distinctCall
            || (distinctCall.Method.GetGenericMethodDefinition() != QueryableMethods.Distinct
                && distinctCall.Method.GetGenericMethodDefinition() != EnumerableMethods.Distinct)
            || distinctCall.Arguments.Count != 1)
            return false;

        if (Unwrap(distinctCall.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } selectCall
            || (selectCall.Method.GetGenericMethodDefinition() != QueryableMethods.Select
                && selectCall.Method.GetGenericMethodDefinition() != EnumerableMethods.Select)
            || selectCall.Arguments.Count != 2
            || !TryResolveOptionalAccumulatorSourceCondition(
                selectCall.Arguments[0], groupingParameter, keyParts, isComposite, translator, out var elementCondition))
            return false;
```

(Note: `Select`'s own generic-method check is ALSO widened to accept the `Enumerable` form — a hand-written
unit test's `g.Distinct().Select(...)` compiles to `Enumerable.Select`, same reasoning as `Where`/`Distinct`
above; the ORIGINAL code only checked `QueryableMethods.Select`, which happened to still work for the
EXISTING, narrower `g.Select(...).Distinct()` tests only because... actually check this empirically in Step 4
below — if an existing passing test regresses here, the fix is exactly this widening, already written above.)

Finally, wrap the accumulator's operand with `$cond` when `elementCondition` is non-null. Replace the existing
commit lines (currently lines 550–558):

```csharp
        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form) — same
        // plain-member-access-only restriction as the ordinary Sum/Average/Min/Max accumulators above; a
        // computed selector falls back.
        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out var operand))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, "$addToSet", operand);
        flattenRead = isSize
            ? new MongoSizeExpression(outputField, call.Method.ReturnType)
            : new MongoArrayReduceExpression(reduceOp!, outputField, call.Method.ReturnType);
        return true;
    }
```

with:

```csharp
        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form) — same
        // plain-member-access-only restriction as the ordinary Sum/Average/Min/Max accumulators above; a
        // computed selector falls back.
        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out MongoExpression? operand))
            return false;

        // EF-322 SP3: a leading g.Where(pred) hop wraps the operand with $cond, using "$$REMOVE" as the else
        // branch so a non-matching element contributes nothing to the $addToSet at all (not a null entry —
        // see this plan's own "Verified design decision"). A leading g.Distinct() hop (or no hop at all)
        // needs no wrapping — elementCondition stays null.
        if (elementCondition is not null)
        {
            operand = new MongoConditionalExpression(
                elementCondition, operand, new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, operand.Type));
        }

        accumulator = new MongoGroupAccumulator(outputField, "$addToSet", operand);
        flattenRead = isSize
            ? new MongoSizeExpression(outputField, call.Method.ReturnType)
            : new MongoArrayReduceExpression(reduceOp!, outputField, call.Method.ReturnType);
        return true;
    }
```

(`operand`'s declared type changes from the implicit `MongoFieldExpression` to explicit `MongoExpression?` at
the `TryTranslateField` call site above, since it may be reassigned to a `MongoConditionalExpression`.)

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests PASS, including every pre-existing `TryBindDistinctAccumulator`-related test (e.g. the
plain `g.Select(...).Distinct().Max()` shape with NO extra hop) — if any pre-existing test fails here, read its
failure carefully before assuming Step 3's widened `Select`/`Distinct` generic-method check broke it; the fix
is almost certainly widening one more check to accept both `Queryable`/`Enumerable` forms, not reverting the
change.

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
git commit -m "EF-322: admit an extra Where/Distinct hop before GroupBy(key).Select(...).Distinct().<Op>()"
```

---

### Task 3: Verify the target spec-test methods, add functional coverage, run every suite

**Files:**
- Test (read-only unless a diff is found): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
  NorthwindGroupByQueryMongoTest.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` — add functional
  coverage for the two shapes this file has none of yet: a predicated `Count`/filtered `Sum` and the
  extra-hop-before-Distinct shapes

**Interfaces:**
- Consumes: Task 1 + Task 2's production changes — this task is pure verification plus new test coverage.

- [ ] **Step 1: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

- [ ] **Step 2: Run the 4 target methods under default (`Native`) mode; regenerate baselines that changed**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_multiple_Count_with_predicate|FullyQualifiedName~GroupBy_constant_with_where_on_grouping_with_aggregate_operators|FullyQualifiedName~GroupBy_group_Distinct_Select_Distinct_aggregate|FullyQualifiedName~GroupBy_group_Where_Select_Distinct_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp3-native.log 2>&1
tail -60 /tmp/sp3-native.log
```

For any method whose `AssertMql` fails with `Assert.Equal() Failure: Strings differ` (a baseline mismatch, NOT
a data/behavior failure — the native pipeline is very unlikely to match the driver-LINQ fallback's own baseline
MQL shape byte-for-byte, e.g. `GroupBy_constant_with_where_on_grouping_with_aggregate_operators`'s current
fallback baseline uses an entirely different `_elements`/`$push`/`$filter`/`$map` structure), regenerate:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm each diff shows a `$cond`/`"$$REMOVE"` shape consistent with this plan's design (a `$group` whose
accumulator input is `{"$cond": [...]}`, `"else"` either a literal `0` (Count) or the string `"$$REMOVE"`
(Sum/Min/Max/Average/addToSet)), then rebuild and rerun WITHOUT the env var to confirm green.

- [ ] **Step 3: Run the same 4 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_multiple_Count_with_predicate|FullyQualifiedName~GroupBy_constant_with_where_on_grouping_with_aggregate_operators|FullyQualifiedName~GroupBy_group_Distinct_Select_Distinct_aggregate|FullyQualifiedName~GroupBy_group_Where_Select_Distinct_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp3-nativeonly.log 2>&1
tail -20 /tmp/sp3-nativeonly.log
```

Expected: all 4 methods (8 with async) now PASS under `NativeOnly`.

- [ ] **Step 4: Re-measure the whole class's `NativeOnly` failure count, checking CONTENT not just the number**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp3-full-nativeonly.log 2>&1
tail -8 /tmp/sp3-full-nativeonly.log
grep -E "^\s*Failed MongoDB" /tmp/sp3-full-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: failure count drops from 42 (SP2's ending count) to 34 (42 − 8), and the method-NAME list is exactly
SP2's 21 remaining methods minus these 4 (i.e. 17 names). Do not just check the NUMBER — SP2's final review
found a "matching count, wrong content" surprise (`Select_GroupBy_All` secretly already fixed, masked by an
unrelated new failure at the same total); diff the actual name list against SP2's own final list before
proceeding.

- [ ] **Step 5: Add functional coverage for the two shapes this file has none of yet**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`. Find its LAST test method
(search for the final `[Fact]` before the file's closing `}`) and add these two tests immediately after it,
before the closing brace:

```csharp
    [Fact]
    public void GroupBy_Count_with_predicate_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "D");

        (string Key, int Small, int Large)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Small = g.Count(o => o.Amount < 100), Large = g.Count(o => o.Amount >= 100) })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Small, r.Large)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200 (both >=100, Large), UK 50+25 (both <100, Small), FR 300 (>=100, Large).
        Assert.Equal([("FR", 0, 1), ("UK", 2, 0), ("US", 0, 2)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Small = g.Count(o => o.Amount < 100), Large = g.Count(o => o.Amount >= 100) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Small, r.Large)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_Where_then_Sum_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "D");

        (string Key, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Total = g.Where(o => o.Amount > 60).Sum(o => o.Amount) })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Total)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200, UK 50+25, FR 300 (see this file's own SeedOrders). Only amounts > 60
        // contribute: US=100+200=300, UK=0 (both 50 and 25 excluded), FR=300.
        Assert.Equal([("FR", 300m), ("UK", 0m), ("US", 300m)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Total = g.Where(o => o.Amount > 60).Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Total)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }
```

- [ ] **Step 6: Run the new functional tests, then the whole Functional suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests" --logger "console;verbosity=normal" \
  > /tmp/sp3-functional-groupby.log 2>&1
tail -10 /tmp/sp3-functional-groupby.log
```

Expected: PASS, 0 failed, including the 2 new tests. If `GroupBy_Where_then_Sum_matches_driver_linq`'s expected
tuple doesn't match — `SeedOrders()`'s actual amounts may differ from what's assumed above; read the file's own
`SeedOrders()` definition and recompute the expected values from the REAL seed data rather than adjusting the
assertion to whatever the run produces.

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp3-functional-full.log 2>&1
tail -8 /tmp/sp3-functional-full.log
```

Expected: PASS, 0 failed (the FULL suite, not just `NativeGroupByTests`).

- [ ] **Step 7: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp3-spec-full.log 2>&1
tail -8 /tmp/sp3-spec-full.log
```

Expected: PASS, 0 failed. If anything OUTSIDE the 4 target methods fails, investigate whether it's a genuine
regression or another "secretly-already-fixed, baseline-only" bonus (SP1's `GroupBy_Property_Select_Sum` and
SP2's `Select_GroupBy_All` were both this pattern) before assuming either way.

- [ ] **Step 8: Run all three suites on EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" -v quiet
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" -v quiet

for CFG in EF8 EF9; do
  echo "=== $CFG unit ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug $CFG" --no-build 2>&1 | tail -5
  echo "=== $CFG spec (GroupBy class) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -5
  echo "=== $CFG functional (GroupBy/Distinct) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests|FullyQualifiedName~NativeDistinctTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all three commands.

- [ ] **Step 9: Commit any baseline changes from Step 2, plus the new functional tests**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-322: verify SP3 conditional-accumulator slice — baselines, functional coverage, full suites"
```

---

## After this plan

This plan covers SP3 only (4 tests). SP4–SP9 each get their own plan when their turn comes, using this slice's
post-landing `NativeOnly` failure count (34, confirmed in Task 3 Step 4) as their new baseline. Before starting
SP4's plan, re-verify SP4's own target tests' shapes against the actual EF Core base-test source the same way
this plan's own investigation (and SP2's own correction) did — don't assume the design doc's original bucketing
is exact without a quick re-check.
