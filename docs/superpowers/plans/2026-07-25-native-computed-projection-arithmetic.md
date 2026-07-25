# Native arithmetic computed projections — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a terminal anonymous/DTO `Select` whose leaves are numeric arithmetic (`+ - * %`, and `/` for floating/decimal) go native, emitting a computed `$project` and materializing through the existing DOM shaper.

**Architecture:** Add one value-expression entry point to `MongoExpressionTranslator` (`TryTranslateValue`) that delegates to the existing, tested `TranslateOperand` arithmetic machinery, plus two divergence guards (integer-division, value-converter/representation). Wire it into `NativeProjectionBinder.TryTranslateLeaf`, gated to arithmetic-binary leaves only (projection-safety: an arithmetic leaf always renders as an operator document, never a bare value that `$project` would misread as an inclusion flag). The `$project` emit side (`MongoPipelineFactory.RenderProject` → `MongoAggregationExpressionRenderer`) and the DOM read-back (`MongoProjectionBindingRemovingExpressionVisitor`'s non-property raw-element branch) already handle computed values unchanged.

**Tech Stack:** C#, EF Core provider, MongoDB C# driver, xUnit (plain `Assert.*`; FluentAssertions is NOT referenced in test projects).

## Global Constraints

- Multi-EF: builds must stay green for `Debug EF8`, `Debug EF9`, `Debug EF10`. No `#if` is expected (all touched types are `internal`, identical across versions) — do not add one unless a build genuinely requires it.
- `<Nullable>enable</Nullable>` on `src/` — annotate new members.
- Preserve file BOMs.
- Commit-message + PR-title prefix: `EF-347: <description>`.
- Tests run serially; each functional test uses a uniquely-named collection.
- The only reliable "goes native" signal is `MongoQueryMode.NativeOnly` (succeeds ⇒ native; a fallback shape throws `NativeTranslationNotSupportedException`). Asserting `$project` MQL shape under `Native` does NOT prove native (a fallback can emit an identical `$project`).
- This slice is the **plain-`Select` path only**. Do NOT touch `NativeSelectManyBinder` — SelectMany computed-leaf is the next slice.
- Spec-test baselining rule: never `[Skip]`; a genuinely-failing ported spec is baselined green via `// Fails …` + doc entry (not relevant unless a flip regresses one).

---

### Task 1: Spike — confirm the three load-bearing assumptions

No production code ships from this task. It de-risks the guard boundary and the shaper wiring BEFORE Task 2/3. Findings go to a gitignored note; the disposition decisions feed Tasks 2–4.

**Files:**
- Create (gitignored, NOT committed): `.superpowers/sdd/EF-347-computed-projection-spike.md`
- Temporary throwaway edits (reverted at task end): `Query/NativeTranslation/NativeProjectionBinder.cs`

**Interfaces:**
- Produces: a written disposition for (a) integer-division/modulo guard boundary, (b) forward-binding routing, (c) nullable/constant behavior — consumed by Tasks 2–4 as the authoritative guard spec.

- [ ] **Step 1: Probe the driver-LINQ oracle for integer division and modulo (incl. negatives).**

Write a throwaway functional probe (or reuse `NativeExprComparisonTests`' Alice/Bob/Carol seed — Alice Age=7 Score=2, Carol Age=-7 Score=2) that runs, under `MongoQueryMode.DriverLinq`, a projection `Select(c => new { D = c.Age / c.Score, M = c.Age % c.Score })` and captures the emitted MQL and the materialized values. Record:
- Does the driver emit a bare `$divide` (yielding 3.5 for 7/2) or wrap it (`$trunc`/`$toInt`) to match C# integer truncation (3)?
- What does the driver do for `%` with a negative dividend (C# `-7 % 2 == -1`)?

Expected/likely: the driver emits bare `$divide` and integer division diverges from C# ⇒ **guard out integer-result `Divide`**. Confirm whether integer `%` (esp. negative) also diverges ⇒ if so, guard out integer `Modulo` too; if it matches C#, keep `%`.

- [ ] **Step 2: Probe forward-binding routing for a computed leaf.**

Temporarily edit `NativeProjectionBinder.TryTranslateLeaf` to accept an arithmetic-binary leaf (call the existing `TranslateComparison` machinery is not enough — for the spike, hand-build a `MongoBinaryExpression` for `c.Age * c.Score` via a quick inline translate, or temporarily expose `TranslateOperand`). Run `Select(c => new { X = c.Age * c.Score })` under `MongoQueryMode.NativeOnly` and confirm it (a) does not throw, (b) emits `$project: { X: { $multiply: ["$Age","$Score"] }, _id: 0 }`, and (c) materializes the correct value — i.e. the leaf reaches `MongoProjectionBindingRemovingExpressionVisitor`'s non-property raw-element branch (lines ~122-131), NOT the LINQ-v3 push-down/mixed path. If it is pre-empted, record exactly where (`MongoProjectionBindingExpressionVisitor` / `ProjectionAnalyzer.CanPushDown`) and what wiring Task 3 must add.

- [ ] **Step 3: Confirm the bare-constant `$project` gotcha and nullable parity.**

Confirm that a bare constant projection leaf (`new { X = 5 }`) rendered via `MongoAggregationExpressionRenderer` would produce `{ X: 5 }`, which `$project` misreads as an inclusion flag (not a literal) — validating the Task 3 decision to gate the new path to arithmetic-**binary** leaves only. Probe a nullable-operand arithmetic leaf (`new { X = c.NullableAge * 2 }` with a null row) under `Native` vs `DriverLinq` and record whether results match (they should: `$multiply` with null → null, C# `int? * 2` with null → null) or need a guard.

- [ ] **Step 4: Write the spike note and revert the throwaway edits.**

Write `.superpowers/sdd/EF-347-computed-projection-spike.md` with the three dispositions. Then `git checkout -- src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` to revert the throwaway edits. Confirm `git status` shows no staged/modified production files.

Run: `git status --short`
Expected: clean working tree except the untracked (gitignored) spike note.

- [ ] **Step 5: STOP for review.** Report the three dispositions; the reviewer confirms the guard boundary before Task 2.

---

### Task 2: `MongoExpressionTranslator.TryTranslateValue` + divergence guards

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Consumes: existing `private MongoExpression? TranslateOperand(Expression)`; `NativeGroupByBinder.HasDefaultKeySerialization(IProperty)` (internal static, same assembly).
- Produces: `public bool TryTranslateValue(Expression valueBody, out MongoExpression? result)` — translates a numeric value expression (member / constant / parameter / arithmetic `+ - * / %`) to a `MongoExpression`, returning `false` for: any non-numeric or non-value shape (via `TranslateOperand`), an integer-result `Divide` anywhere in the tree (guard A), or an operand property lacking default serialization (guard B). Consumed by Task 3.

- [ ] **Step 1: Write failing unit tests.**

Add to `MongoExpressionTranslatorTests.cs` (follow the file's existing pattern for building a translator over a test entity type and a parameter lambda body). Tests (adjust the exact guard set per the Task 1 spike disposition — this shows the expected default):

```csharp
[Fact]
public void TryTranslateValue_multiply_of_two_int_fields_translates_to_binary_multiply()
{
    var (translator, body) = BuildValueBody<Order>(o => o.Price * o.Qty); // int * int
    Assert.True(translator.TryTranslateValue(body, out var expr));
    var binary = Assert.IsType<MongoBinaryExpression>(expr);
    Assert.Equal(MongoBinaryOperator.Multiply, binary.Operator);
}

[Fact]
public void TryTranslateValue_subtract_translates()
{
    var (translator, body) = BuildValueBody<Order>(o => o.Gross - o.Tax);
    Assert.True(translator.TryTranslateValue(body, out var expr));
    Assert.Equal(MongoBinaryOperator.Subtract, Assert.IsType<MongoBinaryExpression>(expr).Operator);
}

[Fact]
public void TryTranslateValue_integer_division_is_rejected() // guard A
{
    var (translator, body) = BuildValueBody<Order>(o => o.Price / o.Qty); // int / int
    Assert.False(translator.TryTranslateValue(body, out _));
}

[Fact]
public void TryTranslateValue_floating_division_is_accepted()
{
    var (translator, body) = BuildValueBody<Order>(o => o.Weight / o.Count); // double / int -> double
    Assert.True(translator.TryTranslateValue(body, out var expr));
    Assert.Equal(MongoBinaryOperator.Divide, Assert.IsType<MongoBinaryExpression>(expr).Operator);
}

[Fact]
public void TryTranslateValue_string_concat_is_rejected() // Add on strings is not numeric
{
    var (translator, body) = BuildValueBody<Order>(o => o.Tag + "!");
    Assert.False(translator.TryTranslateValue(body, out _));
}

[Fact]
public void TryTranslateValue_value_converted_operand_is_rejected() // guard B
{
    // Order.EncStatus is configured with a value converter in the test model builder.
    var (translator, body) = BuildValueBody<Order>(o => o.EncStatus + o.Qty);
    Assert.False(translator.TryTranslateValue(body, out _));
}
```

`BuildValueBody<T>` is a small local helper mirroring how the existing tests construct a translator + extract a lambda body; if the file lacks one, add it alongside the existing setup. `Order` must expose `int Price/Qty/Gross/Tax/Count`, `double Weight`, `string Tag`, and a value-converted `EncStatus`; extend the file's existing test entity or add one following its convention.

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests.TryTranslateValue"`
Expected: FAIL — `TryTranslateValue` does not exist.

- [ ] **Step 3: Implement `TryTranslateValue` + guards.**

Add to `MongoExpressionTranslator` (near `TryTranslate`/`TryTranslateField`):

```csharp
/// <summary>
/// Attempts to translate a numeric VALUE expression (a projection/computed leaf) to a
/// <see cref="MongoExpression"/> — a member field-ref, constant/parameter, or arithmetic
/// (<c>+ - * / %</c>) over numeric operands — reusing the same operand machinery a
/// comparison's operands use. Unlike <see cref="TryTranslate"/> (predicate/boolean shapes),
/// this accepts a bare value. Returns <see langword="false"/> for a non-numeric/non-value shape,
/// an integer-result division (MongoDB <c>$divide</c> is non-truncating, diverging from C#), or an
/// operand whose property is not default-serialized (a computed value over a converted/represented
/// stored form would diverge from CLR arithmetic).
/// </summary>
public bool TryTranslateValue(Expression valueBody, [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    var node = Unwrap(valueBody);

    // Guard A: reject any integer-result division in the subtree (spike-confirmed divergence).
    if (ContainsIntegerDivision(node))
        return false;

    var translated = TranslateOperand(node);
    if (translated is null)
        return false;

    // Guard B: reject a value-converted / non-default-BsonRepresentation operand.
    if (!AllFieldsDefaultSerialized(translated))
        return false;

    result = translated;
    return true;
}

private static bool ContainsIntegerDivision(Expression node)
    => node switch
    {
        BinaryExpression { NodeType: ExpressionType.Divide } d
            => IsIntegerType(d.Type) || ContainsIntegerDivision(d.Left) || ContainsIntegerDivision(d.Right),
        BinaryExpression b => ContainsIntegerDivision(b.Left) || ContainsIntegerDivision(b.Right),
        UnaryExpression u => ContainsIntegerDivision(u.Operand),
        _ => false
    };

private static bool IsIntegerType(Type type)
{
    var t = Nullable.GetUnderlyingType(type) ?? type;
    return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
}

private static bool AllFieldsDefaultSerialized(MongoExpression expr)
    => expr switch
    {
        MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
        MongoBinaryExpression b => AllFieldsDefaultSerialized(b.Left) && AllFieldsDefaultSerialized(b.Right),
        _ => true
    };
```

**Spike disposition (Task 1, confirmed live):** integer `Modulo` does NOT diverge — MongoDB `$mod` matches C#'s truncating / dividend-sign semantics exactly, including a negative dividend (`-7 % 2 == -1` both). So guard **integer `Divide` only**; `Modulo`/`Add`/`Subtract`/`Multiply` ship unguarded. `ContainsIntegerDivision` as written above is correct — do NOT extend it to `Modulo`.

- [ ] **Step 4: Run tests to verify they pass.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests.TryTranslateValue"`
Expected: PASS (all 6).

- [ ] **Step 5: Verify existing translator tests still pass (comparison path unchanged).**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"`
Expected: PASS — no regression in the EF-329 comparison/operand tests (proves `TranslateOperand` is untouched).

- [ ] **Step 6: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs
git commit -m "EF-347: Add TryTranslateValue for numeric computed leaves"
```

- [ ] **Step 7: STOP for review.**

---

### Task 3: Wire computed leaves into `NativeProjectionBinder`

> **⚠️ SPIKE-CONFIRMED SILENT-WRONG-DATA BUG — this task has TWO production edits, not one.** Task 1 proved that populating `Select.Projection` alone is NOT enough: the shaper's projection mapping is built by a SEPARATE path — `MongoProjectionBindingExpressionVisitor.Visit` — which has no `BinaryExpression` case, so it decomposes `c.Age * c.Score` into two `MemberExpression` visits that clobber the same `ProjectionMember` dictionary slot, producing **silent wrong data (`(Age*Score)²`) under BOTH `Native` and `NativeOnly`**. This task MUST add a `BinaryExpression`-arithmetic case to that visitor (Step 3b) that registers the whole node as ONE leaf. The unit tests here (Projection populated) will NOT catch this bug — only Task 4's functional value-correctness test will — so both edits ship together and Task 4 is the proof.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (`TryTranslateLeaf`, ~lines 99-120)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (`Visit`, new arithmetic `BinaryExpression` case beside the `MemberExpression` case at ~lines 144-148)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs` (confirm this is where terminal-projection binding is unit-tested; if a dedicated `NativeProjectionBinderTests.cs` is more appropriate to the file's existing conventions, create it)

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslateValue` (Task 2).
- Produces: `NativeProjectionBinder.TryPopulateNativeProjection` now populates `Select.Projection` for an arithmetic-binary leaf (no signature change); `MongoProjectionBindingExpressionVisitor.Visit` now maps an arithmetic `BinaryExpression` projection leaf to a single `ProjectionBindingExpression`.

- [ ] **Step 1: Write failing unit tests.**

Add tests asserting that a `Select` whose leaf is arithmetic populates `Select.Projection` (and `Route == NativeRoute.Projection`), while out-of-scope leaves do not. Follow the file's existing pattern for driving `TryPopulateNativeProjection` (or the QMTEV `TranslateSelect`) and inspecting `mongoQ.Select`:

```csharp
[Fact]
public void Arithmetic_leaf_populates_native_projection()
{
    var mongoQ = BuildQuery<Order>();
    var selector = Selector<Order>(o => new { Total = o.Price * o.Qty });
    Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
    var p = Assert.Single(mongoQ.Select.Projection);
    Assert.Equal("Total", p.Alias);
    Assert.IsType<MongoBinaryExpression>(p.Expression);
    Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
}

[Fact]
public void String_concat_leaf_does_not_populate_projection()
{
    var mongoQ = BuildQuery<Order>();
    Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, Selector<Order>(o => new { X = o.Tag + "!" })));
    Assert.Empty(mongoQ.Select.Projection);
}

[Fact]
public void Integer_division_leaf_does_not_populate_projection()
{
    var mongoQ = BuildQuery<Order>();
    Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, Selector<Order>(o => new { X = o.Price / o.Qty })));
    Assert.Empty(mongoQ.Select.Projection);
}

[Fact]
public void Bare_constant_leaf_does_not_populate_projection() // projection-safety: $project would misread {X:5}
{
    var mongoQ = BuildQuery<Order>();
    Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, Selector<Order>(o => new { X = 5 })));
    Assert.Empty(mongoQ.Select.Projection);
}

[Fact]
public void Mixed_field_and_arithmetic_leaves_both_populate()
{
    var mongoQ = BuildQuery<Order>();
    Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, Selector<Order>(o => new { o.Tag, Total = o.Price * o.Qty })));
    Assert.Equal(2, mongoQ.Select.Projection.Count);
}
```

`BuildQuery<T>`/`Selector<T>` mirror the file's existing helpers (reuse them; do not invent new infra if equivalents exist).

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~SlotPopulationTests.Arithmetic_leaf|FullyQualifiedName~SlotPopulationTests.Integer_division|FullyQualifiedName~SlotPopulationTests.Bare_constant|FullyQualifiedName~SlotPopulationTests.String_concat|FullyQualifiedName~SlotPopulationTests.Mixed_field"`
Expected: FAIL — `Arithmetic_leaf_populates_native_projection` fails (leaf rejected today); the negative-case tests may already pass (they fall back today too) — the point is they must STILL pass after the change.

- [ ] **Step 3: Implement the wiring in `TryTranslateLeaf`.**

In `NativeProjectionBinder.TryTranslateLeaf`, after the existing `MemberExpression` field check and the `TryTranslateProjectedCollectionCount` check, before `result = null!; return false;`:

```csharp
// Arithmetic computed leaf (EF-347): a numeric (+ - * / %) projection leaf renders as an aggregation
// operator document (e.g. { $multiply: [...] }) via MongoAggregationExpressionRenderer, and the DOM shaper
// reads it back raw by alias. Gate to a BINARY arithmetic top node only — a bare constant/parameter leaf
// would render as a bare value that $project misreads as an inclusion flag; TryTranslateValue's numeric-type
// and divergence guards handle string-concat / integer-division / converted operands.
if (leafExpression is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
        or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo }
    && translator.TryTranslateValue(leafExpression, out var computed))
{
    result = computed;
    return true;
}
```

Ensure `using System.Linq.Expressions;` is present.

- [ ] **Step 3b: Add the arithmetic `BinaryExpression` case to `MongoProjectionBindingExpressionVisitor.Visit` (the silent-wrong-data fix).**

In `Query/Visitors/MongoProjectionBindingExpressionVisitor.cs`, in the `Visit(Expression)` switch, add a case beside the existing `MemberExpression` case (~lines 144-148), mirroring it — register the WHOLE binary node as one leaf so it is NOT decomposed into two clobbering `MemberExpression` visits:

```csharp
// Arithmetic computed projection leaf (EF-347): register the whole binary node as ONE projection
// leaf, exactly like a MemberExpression, so it maps to a single ProjectionMember slot. Without this,
// the default walk would visit each operand's MemberExpression separately, both writing the SAME
// current ProjectionMember and silently producing wrong data ((A*B)² instead of A*B). Gated to the
// same arithmetic operators NativeProjectionBinder accepts; a rejected leaf (e.g. string concat)
// makes the whole projection fall back to driver-LINQ, which uses its own shaper, so this mapping
// is only ever consumed on the native path.
case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
        or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo } binaryExpression:
    var arithProjectionMember = GetCurrentProjectionMember();
    _projectionMapping[arithProjectionMember] = binaryExpression;
    return new ProjectionBindingExpression(_queryExpression, arithProjectionMember, expression.Type);
```

Ensure `using System.Linq.Expressions;` is present (it typically already is).

- [ ] **Step 4: Run tests to verify they pass.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~SlotPopulationTests"`
Expected: PASS — new tests pass, all pre-existing projection-binding tests still pass.

- [ ] **Step 5: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs
git commit -m "EF-347: Bind arithmetic computed projection leaves to native \$project"
```

- [ ] **Step 6: STOP for review.**

---

### Task 4: End-to-end functional coverage (native + parity + NativeOnly + guard fallbacks)

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs`

**Interfaces:**
- Consumes: the native computed-projection path (Tasks 2-3). Mirrors `NativeExprComparisonTests` structure (`TemporaryDatabaseFixture`, `SingleEntityDbContext`, `UseQueryMode`, `Mql(logs)` helper).

- [ ] **Step 1: Write the functional tests.**

Create `NativeComputedProjectionTests.cs` modeled on `NativeExprComparisonTests` (copy its seed/context/`Mql` scaffolding; entity `Customer { ObjectId Id; string Name; int Age; int Score; double Weight; int? MaybeAge; }`). Cover, each asserting: (i) succeeds under `NativeOnly` (proves native), (ii) `Native` result set == `DriverLinq` result set (parity), (iii) for the emit assertion, the `$project` MQL contains the expected operator:

```csharp
[Fact] public void Multiply_projection_goes_native_and_matches_driver() { /* new { P = c.Age * c.Score }; assert $multiply, NativeOnly ok, parity */ }
[Fact] public void Subtract_projection_goes_native() { /* c.Age - c.Score → $subtract */ }
[Fact] public void Add_projection_goes_native() { /* c.Age + c.Score → $add */ }
[Fact] public void Modulo_projection_goes_native() { /* c.Age % c.Score → $mod (verify negatives via Carol, per Task 1 disposition) */ }
[Fact] public void Floating_division_projection_goes_native() { /* c.Weight / c.Score → $divide, double result */ }
[Fact] public void Mixed_member_and_arithmetic_projection_goes_native() { /* new { c.Name, T = c.Age * c.Score } */ }
[Fact] public void Nullable_operand_arithmetic_matches_driver() { /* new { X = c.MaybeAge * 2 } with a null row; Native==DriverLinq */ }

[Fact] public void Integer_division_projection_falls_back_gracefully_except_under_NativeOnly()
{ /* c.Age / c.Score: Native & DriverLinq return CORRECT truncated ints (equal), NativeOnly throws NativeTranslationNotSupportedException */ }

[Fact] public void String_concat_projection_falls_back_gracefully_except_under_NativeOnly()
{ /* new { X = c.Name + "!" }: Native==DriverLinq correct, NativeOnly throws */ }
```

Use the `NativeExprComparisonTests` idiom: run under `NativeOnly` in a `try` asserting success; run under `Native` and `DriverLinq` and assert equal ordered result lists; capture MQL via `Mql(logs)` and `Assert.Contains("$multiply", mql)` etc.

- [ ] **Step 2: Run the new functional tests.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeComputedProjectionTests"`
Expected: PASS (all). If integer division does NOT throw under `NativeOnly`, the Task 2 guard is wrong — revisit.

- [ ] **Step 3: Commit.**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs
git commit -m "EF-347: Functional coverage for native arithmetic computed projections"
```

- [ ] **Step 4: STOP for review.**

---

### Task 5: Test flips (known + swept), AGENTS.md as-built note, and full-matrix verification

**Files:**
- Modify: the **4 known** now-failing functional/gate tests (behavior-change fallout from Tasks 2-3 — legit flips, controller-triaged):
  - `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeGateTests.cs` (~line 187-190: the `Select(c => new { Doubled = c.Score * 2 })` test asserting "computed projection is not natively representable → throws under NativeOnly")
  - `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGateRoutingTests.cs` (`D_computed_projection_throws_under_NativeOnly`, ~line 460-466: `Select(c => new { Doubled = c.Age * 2 })`)
  - `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` (**2** tests — a computed-leaf projection after a set op that asserted graceful fallback / NativeOnly-throws, e.g. `Computed_leaf_projection_after_union_falls_back_gracefully` and its Intersect analog; the binder change is global so a set-op trailing computed leaf now goes native)
- Modify (any further flips the sweep surfaces): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/*.cs` (Northwind arithmetic-projection overrides)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:** none (test updates + docs + verification).

- [ ] **Step 0: Update the 4 known flips — VERIFY each newly-native shape is CORRECT, don't just make the test pass.**

For EACH of the 4 tests above: the shape (`c.Score * 2` / `c.Age * 2` / set-op trailing computed leaf) now goes native. Update the test to assert the NEW correct behavior — under `NativeOnly` it now SUCCEEDS (native), and where a driver-LINQ oracle exists assert `Native == DriverLinq` parity with the correct computed values. Do NOT merely flip `Assert.Throws` → nothing; assert the query returns the correct computed results. If a test's PURPOSE was specifically to demonstrate a *non-native* projection (e.g. `D_computed_projection_throws_under_NativeOnly` exists to show a fallback), REPURPOSE it to a still-non-native computed shape (string concat `c.Name + "!"`, or a `Math.*`/string-method leaf) so the "computed long tail still falls back" coverage is preserved — rename accordingly. For the 2 NativeSetOpsTests: confirm under `NativeOnly` the set-op-then-computed-projection now succeeds AND returns correct values (a set-op trailing computed leaf materializing correctly through the shared binder), and update the test + its class-doc wording. Run each updated test under a live MongoDB to confirm green.

- [ ] **Step 1: Find Northwind spec tests that now flip to native.**

Search the Northwind projection spec suite for arithmetic-projection cases (e.g. `Select_... => new { ... o.X * ... }`) whose overrides currently assert a driver-LINQ shape or a fallback. Run the spec suite under `MONGODB_EF_NATIVE_ONLY=1` before/after to see which projection cases move from throwing to passing:

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSelectQueryMongoTest"`
For each case that now goes native, update its override to assert the native MQL / success (following the existing override convention in that file). If none flip (arithmetic projections may not appear in the Northwind selector suite), record that the gain is proven solely by `NativeComputedProjectionTests` — do not manufacture a flip.

- [ ] **Step 2: Write the AGENTS.md as-built note and correct the stale wording.**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`:
- Add a dedicated "Native arithmetic computed projections (EF-347)" note (mirroring the other EF-347 notes' style) describing: the `TryTranslateValue` entry point delegating to `TranslateOperand`; the arithmetic-binary-leaf gate in `NativeProjectionBinder`; guard A (integer division) and guard B (converter/representation); the graceful-fallback-with-oracle disposition; and the deferred set (SelectMany computed-leaf = next slice; string/date/Math/cast/unary long tail).
- Update the SP3 as-built-scope paragraph wording — "**computed leaves** (arithmetic/method calls inside the projection) … still fall back" — to reflect that numeric arithmetic computed leaves now go native (method calls / string / date / cast remain deferred).
- **Correct the integer-division disposition wording** (design doc said "Native & DriverLinq return CORRECT truncated ints" — that is WRONG, per Task-4 live finding): integer-result division is guarded out of native; for a NON-exact quotient it throws `MongoDB.Bson.TruncationException` in BOTH `Native` and `DriverLinq` (MongoDB `$divide` is non-truncating and the driver's Int32 deserializer rejects the resulting double), and `NativeTranslationNotSupportedException` under `NativeOnly`. So it is NOT a "graceful fallback returning correct values" — it declines to native and the driver-LINQ path itself also fails on a non-exact integer quotient. Word the AGENTS.md note and the design doc accordingly.

- [ ] **Step 3: Commit the docs + any spec flips.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md tests/MongoDB.EntityFrameworkCore.SpecificationTests/
git commit -m "EF-347: Doc native arithmetic computed projections + spec flips"
```

- [ ] **Step 4: Full 3-version verification (controller runs foreground per process lesson).**

Run the `/test-all` skill (build + test EF8, EF9, EF10). Confirm zero regressions vs the `5729436` baseline (EF8 7341/67, EF9 7702/68, EF10 7299/71) — expect all three GREEN with pass counts ≥ baseline. Also run the `NativeOnly` spec sweep to confirm no native regression:

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~SpecificationTests"`
Expected: no new failures vs the 2192P baseline.

- [ ] **Step 5: STOP for review.** Report the 3-version results and any spec flips. After approval: whole-branch review, squash to one commit, and push per the stacked-PR workflow (plain FF onto `origin/NativeQueryOngoing`, keep a `-presquash` backup).

---

## Self-Review

**Spec coverage:**
- Value entry point (`TryTranslateValue` delegating to `TranslateOperand`) → Task 2. ✓
- Binder wiring, arithmetic-binary gate → Task 3. ✓
- Integer-division guard (guard A) → Task 1 (boundary) + Task 2 (impl) + Task 4 (fallback proof). ✓
- Value-converter/representation guard (guard B) → Task 2 + Task 4. ✓
- Emit side already done (RenderProject) — no task needed; verified end-to-end in Task 4. ✓
- Read-back already done — spike Task 1 Step 2 verifies routing; Task 4 proves materialization. ✓
- Oracle/parity + NativeOnly disposition → Task 4. ✓
- Spec flip + AGENTS.md note + stale-wording fix → Task 5. ✓
- Scope boundary (SelectMany deferred, string/date/Math/cast/unary deferred) → enforced by the arithmetic-binary gate + numeric-type check; documented in Task 5. ✓

**Placeholder scan:** No TBD/TODO; every code step shows code; guard set is explicit with a spike-gated extension point (integer modulo) called out precisely. ✓

**Type consistency:** `TryTranslateValue(Expression, out MongoExpression?)`, `MongoBinaryExpression.Operator` (`MongoBinaryOperator`), `Select.Projection` (`IReadOnlyList<MongoProjection>` with `.Alias`/`.Expression`), `Route`/`NativeRoute.Projection`, `NativeGroupByBinder.HasDefaultKeySerialization(IProperty)` — all consistent across tasks and matched to the current codebase. ✓
