# NorthwindSelectQueryMongoTest: every test native (or failing on both paths)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every `NorthwindSelectQueryMongoTest` test passes under `MONGODB_EF_NATIVE_ONLY=1` on EF8, EF9 and EF10, or is
marked `// Fails:` because it fails on **both** native and driver-LINQ. Today 18 tests (36 results) pass only through
the driver-LINQ fallback on EF10, plus one EF8-only test.

**Architecture:** Every failure is a decline in `NativeProjectionBinder.TryPopulateNativeProjection`, reached from
`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` (`:565`). The query then surfaces as
"Query projects a non-entity result" at `MongoShapedQueryCompilingExpressionVisitor.cs:317`. The 18 tests split into nine root-cause
buckets. Each task below is one bucket: one emit-side change in the binder, plus the matching read-side change in
`MongoProjectionBindingExpressionVisitor` / `MongoShapedQueryCompilingExpressionVisitor` where the shaper must read
what the server now computes. Two tasks close latent native crashes that the triage found next to their buckets.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit (plain `Assert.*`),
Docker/TestContainers.

**Spec:** No separate spec doc. The design comes from the 2026-09-30 native-only triage (Background below). Sibling
plans that run in parallel: `2026-09-30-native-setoperations-suite-HANDOFF.md` and
`2026-09-30-native-misc-low-hanging-HANDOFF.md`. See **Cross-plan ownership**.

## Background (measured 2026-09-30 at `8c5f00e1`, branch `EF-322c`)

- Default mode: the Select, SetOperations and Misc suites are all green on EF8/EF9/EF10.
- `MONGODB_EF_NATIVE_ONLY=1`, EF10: 18 Select tests fail (×2 async = 36).
- EF8 only: `Select_with_complex_expression_that_can_be_funcletized` also fails, because its EF8 body is
  `c.ContactName.IndexOf("")`. It is the same guard as Task 3.
- None of the 18 fail under driver-LINQ, so **none qualifies for `// Fails:`**. They all have to go native.

Per-test decline sites were verified by tracing (every `return false` in the binder, the translator and the slot
populator was logged) in a private copy. Trace evidence is in
`/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/9fb445c5-259a-41aa-bdf0-74151f361969/scratchpad/triage-select/traces/`.
NPB = `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`.

| Test | Tree after preprocessing | Decline | Task |
|---|---|---|---|
| `…from_binary_expression_introduces_explicit_cast` | `Convert(o.OrderID+o.OrderID, long)` | NPB:739. The arithmetic arm (:695) needs a top-level `BinaryExpression`, and the cast clause (:732) needs a `MongoFieldExpression`. | 1 |
| `…from_unary_expression_introduces_explicit_cast1` | `Convert(-o.OrderID, long)` | NPB:739 | 1 |
| `…from_unary_expression_introduces_explicit_cast2` | `-Convert(o.OrderID, long)` | NPB:739 | 1 |
| `Select_anonymous_constant_in_expression` | `c.CustomerID.Length + 5` (the PK) | NPB:523 `ReadsNullAsDefault`: `MayBeNull` is true for any reference-typed field, including `_id` | 2 |
| `…from_length_introduces_explicit_cast` | `(long)o.CustomerID.Length` (nullable FK) | NPB:523 | 3 |
| `New_date_time_in_anonymous_type_works` | `new { A = new DateTime() }` | NPB:739 (a closed `NewExpression` has no translate arm) | 4 |
| `Select_anonymous_empty` | `new { }` | NPB:273→251 | 4 |
| `Select_bool_closure` | a bare `QueryParameterExpression` holding an anonymous-type value | NPB:729 `TryProbeBareValueRenders` fails | 4 |
| `Using_enumerable_parameter_in_projection` | `Orders = QueryParameter(List<OrderDto>)` | NPB:729/739 | 4 |
| `Projection_of_entity_type_into_object_array` | `new[] { c }` | NPB:739/961 (`IsClientOnlyWholeEntitySubtree` has no NewArrayInit arm) | 5 |
| `Projection_of_entity_type_into_object_list` | `new List<object> { c }` | same (ListInit) | 5 |
| `Projection_with_parameterized_constructor_with_member_assignment` | `new CustomerWrapper(c) { City = c.City }` | NPB:961 (MemberInit with constructor arguments) | 5 |
| `Project_to_int_array` | `new[] { Convert(e.EmployeeID,int?), e.ReportsTo }` | NPB:739→273, 961→251 | 6 |
| `Project_to_object_array` | `new object[] { …, EF.Property<string>(e,"Title") }` | same | 6 |
| `Projection_of_multiple_entity_types_into_object_array` | `o => new[] { o.Outer, o.Inner }` over a LeftJoin | `NativeJoinScopeProjectionBinder.TryBindProjection` :86 | 6 |
| `Select_datetime_TimeOfDay_component` | `o.OrderDate.Value.TimeOfDay` | `MongoDatePart` has no TimeOfDay (`MongoDatePartExpression.cs:20`) | 7 |
| `Select_entity_compared_to_null` | `o => o.Inner == null` over a LeftJoin | the TranslateSelect join arms (MQTEV:516-545) don't match it | 8 |
| `Projection_custom_type_in_both_sides_of_ternary` | bare `cond ? new IdName{…} : new IdName{…}` | NPB:1379/1474→289→251 (no alias for a DocumentConstruction) | 9 |

## Cross-plan ownership (read before starting any task)

The three plans overlap in **five** places. This table is the single assignment; the other two plans repeat it.

| Shared capability | Owner | Consumer | Coordination |
|---|---|---|---|
| **Negate leaf, emit and read side** (`-x` as a projected value) | **This plan, Task 1** | SetOps: 10 of its 22 tests need exactly this | Land Task 1 **first** and tell the SetOps agent the commit. Task 1 regenerates SetOps baselines that change as a result. SetOps must not implement Negate itself. |
| **Row-independent leaves evaluated client-side** (closures, closed `new`, `new {}`) | **This plan, Task 4** | SetOps Task 2 (constant on source1 of a Union); Misc `MemberInit…funcletized` | Task 4 adds `MongoSelectDefinition.HasClientEvaluatedProjectionLeaf` and makes the set-op gate decline it. SetOps Task 2 only handles `$literal`-rendered constants, which are server-side. The two must not overlap: see Task 4 Step 6. |
| **Null-propagated `Length`/`IndexOf` behind a non-nullable type** (the NPB:523 guard) | **This plan, Task 3** | Misc `Non_nullable_property_through_optional_navigation`; EF8 Select `Select_with_complex_expression_that_can_be_funcletized` | Misc does not touch that test. Task 3 fixes both it and the EF8 one. |
| **MemberInit with parameter-only constructor arguments** | **This plan, Task 4** (it is a row-independent leaf) | Misc `MemberInitExpression_NewExpression_is_funcletized_even_when_bindings_are_not_evaluatable` | Misc skips it. |
| **PK-never-null** | **This plan, Task 2**, *narrow form* | none | The wide form (in `MayBeNull`) changes 29 baselines across GroupBy, Misc, AggregateOperators and Where. The narrow form changes none outside this test, so it cannot collide with the other plans. Do not widen it. |

## Global Constraints

- Work directly on branch `EF-322c` in `/Users/arthur.vickers/code/mongo-efcore-provider` (no worktree). Commit
  messages start with `EF-322: `. **Do NOT push**: the owner decides.
- Preserve file BOMs. `src/` is nullable-enabled; annotate new types.
- Must compile and pass under `Debug EF8`, `Debug EF9` and `Debug EF10`. Use `#if` only where a version genuinely differs.
- xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset. Never enable test parallelization.
- A recognizer must not mutate and then decline: stage into locals and commit only after every gate passes.
- **The gate and the read side call one shared predicate.** They must never be restated separately. This bug class has
  been found repeatedly (memory `ef405-a4-slice`), and Task 1's triage reproduced it: with the emit side alone, the
  shaper re-applied Negate over the server-negated value and returned `+OrderID`.
- Every new native shape gets a functional test in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`, using
  `NativeModeAssert.NativeAndParity` (NativeOnly == DriverLinq). Where driver-LINQ is wrong or throws, assert NativeOnly
  against a hand-computed oracle and say why in a comment.
- Guards must be proven by mutation (disable the guard → a named test fails). Record the result in the commit message.
- Stage only files you changed. Never stage `nuget.config`.
- Scratch logs go in a unique subdirectory per task:
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/9fb445c5-259a-41aa-bdf0-74151f361969/scratchpad/select-task<N>/`.
- Regenerate MQL baselines only with a tight `--filter` (see `tests/…SpecificationTests/AGENTS.md`), then `git diff` and
  rerun without the variable.

**Standard commands** (the `<v>` below stands for EF8, EF9 or EF10):

```bash
P=tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj
FP=tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj
dotnet build MongoDB.EFCoreProvider.sln -c "Debug <v>"
# The Select suite, native-only
MONGODB_EF_NATIVE_ONLY=1 dotnet test $P -c "Debug <v>" --no-build --filter "FullyQualifiedName~NorthwindSelectQueryMongoTest"
# One functional class
dotnet test $FP -c "Debug <v>" --no-build --filter "FullyQualifiedName~<Class>"
```

## Review Focus

1. **A computed leaf the shaper re-applies client-side over an already-computed server value**, for example Negate or Convert
   over `_v`. The result must be the value computed once, not computed twice. Pinned in Task 1: a test asserts the negative
   sign of the result, not just the row count.
2. **A client-evaluated leaf that a later server operator reads** (Union dedup, `Where`, `OrderBy`, a GroupBy key over the
   alias). The query must decline and must not read a missing field as null/default. Pinned by Task 4 decline tests.
3. **A container (`new[]`, `new List<object>`) whose elements have different CLR types or are boxed.** Each element
   must come back with its own runtime type (`AssertArrays` checks `GetType()`). Pinned in Task 6.
4. **An entity-valued argument in a positional constructor, or in a container element, on the scalar path.** It must
   either materialize the entity correctly or decline, never crash. There is a latent crash today; Task 6 pins it.
5. **A null on the nullable side of a null-propagated scalar** (`o.CustomerID.Length` with a null CustomerID, or
   `.Value.TimeOfDay` with a null date). It must never read as 0/default. Pinned in Tasks 3 and 7.

---

### Task 1: Numeric computed leaf under Negate or a widening cast (shared with SetOps)

**Flips:** `…from_binary_expression_introduces_explicit_cast`, `…from_unary_expression_introduces_explicit_cast1` and
`…_cast2`. Also, in default mode, flips 10 SetOps `Union_over_*unary*` tests to native (their baselines change).

**Files:**
- Modify: `NativeProjectionBinder.cs`, `TryTranslateLeafCore` (arithmetic arm around :695, cast clause :732)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (the `case`
  at :273 and `IsNativeComputedLeaf` at :423)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedBareProjectionTests.cs` (extend)
- Baselines: `NorthwindSelectQueryMongoTest.cs`, `NorthwindSetOperationsQueryMongoTest.cs`

**Interfaces:**
- Produces: `NativeProjectionBinder.IsNumericComputedLeafShape(Expression leaf)`, an internal static predicate. It is
  true for a `UnaryExpression{Negate|NegateChecked}` over a numeric operand, and for a widening numeric
  `Convert` over one of those or over an arithmetic `BinaryExpression`. The emit side and the read side must both call it.

- [ ] **Step 1: Write the failing functional tests.** Add these to `NativeComputedBareProjectionTests`, following its seed
  pattern, and assert exact values (with signs):
  - `Select(x => -x.A)` is bare, and wrapped as `new { N = -x.A }`.
  - `Select(x => (long)-x.A)`.
  - `Select(x => -(long)x.A)`: the double-negation trap case.
  - `Select(x => (long)(x.A + x.B))`.
  - Decline control: `Select(x => (short)(x.A + (long)x.B))`. Narrowing must stay declined, because `$toInt`
    semantics differ from C# truncation. Use `NativeModeAssert.DeclinesCleanly`.

  ```csharp
  [Fact]
  public void Negate_over_widening_cast_reads_the_server_value_once()
  {
      var (collection, _) = Seed(nameof(Negate_over_widening_cast_reads_the_server_value_once));
      var results = NativeModeAssert.NativeAndParity(mode =>
      {
          using var db = CreateContext(collection, [], mode);
          return db.Entities.AsNoTracking().OrderBy(x => x.A).Select(x => -(long)x.A).ToList();
      });
      Assert.All(results, r => Assert.True(r <= 0)); // double negation would make these positive
  }
  ```

- [ ] **Step 2: Run the tests and confirm they fail.** Expect `NativeTranslationNotSupportedException` under NativeOnly.
- [ ] **Step 3: Emit side.** Add `IsNumericComputedLeafShape`. In `TryTranslateLeafCore`, before the node-kind
  admit list, add the arm below. Keep the existing `:695` arm as it is.

  ```csharp
  if (IsNumericComputedLeafShape(leafExpression)
      && translator.TryTranslateValue(leafExpression, out var numeric)
      && numeric is MongoBinaryExpression or MongoUnaryExpression)
  {
      result = numeric;
      return true;
  }
  ```

  `-x` translates to `MongoBinaryExpression(Subtract, 0, x)` (`MongoExpressionTranslator.cs:1734-1742`), and the widening
  Convert is dropped by `TryTranslateValue`. Both therefore arrive as a `MongoBinaryExpression`.
- [ ] **Step 4: Read side.** Add `UnaryExpression { NodeType: Negate or NegateChecked }` and widening
  `UnaryExpression { NodeType: Convert }` to the `case` at MPBEV:273, guarded by
  `NativeProjectionBinder.IsNumericComputedLeafShape(expression) && IsNativeComputedLeaf(expression)`. The structural
  equality in `IsNativeComputedLeaf` already restricts it to the exact staged subtree. **Do not** use an operand-shape
  heuristic. An earlier attempt with `IsSimpleArithmeticLeaf` diverged for `new { N = -b.Posts.Count }`.
- [ ] **Step 5: Run the tests and confirm they pass.** Run the functional class and the three Select tests under NativeOnly on EF10.
- [ ] **Step 6: Mutation.** Remove the read-side case → `Negate_over_widening_cast_reads_the_server_value_once` fails
  with positive values. Restore it.
- [ ] **Step 7: Baselines.** Regenerate the Select `…explicit_cast*` baselines (the `$toLong` becomes `$subtract`/`$add`
  shape). Then run `NorthwindSetOperationsQueryMongoTest` in **default** mode and regenerate every baseline that now
  differs; expect about 10 `Union_over_*unary*` tests.
  - Also run AggregateOperators, GroupBy, Misc and Where in default mode. Any new MQL diff must be explained in the commit message.
- [ ] **Step 8: Commit.** Message: `EF-322: native Negate / widening-cast computed projection leaves`. Record the
  commit hash for the SetOps agent.

### Task 2: Primary-key fields never null in `MayBeNullBehindNonNullableType` (narrow)

**Flips:** `Select_anonymous_constant_in_expression`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
  (`MayBeNullBehindNonNullableType`, **not** `MayBeNull` at :958)
- Test: `NativeMaterializerNullabilityTests.cs` (extend)

- [ ] **Step 1: Failing test.** Add `Select(x => new { x.Id, L = x.StringKey.Length + 5 })` on an entity with a
  string PK, asserting `NativeAndParity`. Add a control: the same shape over a nullable, non-key string still
  `DeclinesCleanly`.
- [ ] **Step 2: Run it and confirm it fails.**
- [ ] **Step 3: Implement.** Inside `MayBeNullBehindNonNullableType` only, treat a
  `MongoFieldExpression { NullSafe: false }` whose `Property?.IsPrimaryKey() == true` as non-null. Leave `MayBeNull`
  and the render-time `$ifNull` guards unchanged. That is what keeps the change from touching 29 baselines in other suites.
- [ ] **Step 4: Run and confirm it passes.** Regenerate only this test's baseline if its MQL differs.
- [ ] **Step 5: Mutation.** Drop the PK clause and the new test fails; the control stays green.
- [ ] **Step 6: Commit.** `EF-322: primary-key string leaves are never null behind a non-nullable projection`.

### Task 3: A null-propagated scalar behind a non-nullable type throws like EF instead of declining

> **Owner ruling (2026-09-30): YES.** Today the NPB:523 guard declines. Instead, translate, read the value as
> nullable, and throw `InvalidOperationException("Nullable object must have a value.")` when it is null. That is
> exactly what upstream `Non_nullable_property_through_optional_navigation` asserts, and what EF relational does.
> Driver-LINQ instead sends an unguarded `$strLenCP` that fails server-side on null.

**Flips:** `…from_length_introduces_explicit_cast`, Misc `Non_nullable_property_through_optional_navigation`, and EF8
`Select_with_complex_expression_that_can_be_funcletized`.

**Files:**
- Modify: `NativeProjectionBinder.cs` (`TryTranslateLeaf` :505-523)
- Modify: `MongoAggregationExpressionRenderer.cs` (`ReadsNullAsDefault` / `MayBeNullBehindNonNullableType`)
- Modify: the read side. `MongoProjectionBindingRemovingExpressionVisitor` reads such a leaf via a `T?` serializer, then
  applies a `.Value`-equivalent that throws the EF message.
- Modify: `NorthwindMiscellaneousQueryMongoTest.cs:4148-4158`, the override. Today it asserts only "some exception"
  followed by driver MQL. Make it assert the upstream message, and baseline the native MQL.
- Test: `NativeMaterializerNullabilityTests.cs`

- [ ] **Step 1: Failing tests.** For a nullable string `S`:
  - `Select(x => new { x.S.Length })` over rows with a null `S` throws `InvalidOperationException` with message
    `"Nullable object must have a value."` under NativeOnly.
  - Over rows with no nulls, it returns exact lengths (`NativeAndParity`).
  - The same holds for `(long)x.S.Length` and `x.S.IndexOf("")`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - Replace the `ReadsNullAsDefault` decline with a flag on the staged leaf (for example
    `MongoProjection.ThrowsOnNull`). Set it in the same predicate call so the gate and the reader share it.
  - Render null-safely (`$cond [$eq [field, null]], null, $strLenCP]`, or the existing null-safe form).
  - The reader deserializes as `T?` and throws on `null`.
- [ ] **Step 4: Run and confirm they pass.** Also run the Misc test on all three versions.
- [ ] **Step 5: Mutation.** Drop the throw and the null-row test fails (it reads 0).
- [ ] **Step 6: Commit.** `EF-322: native null-propagated scalars behind non-nullable types throw like EF`.

### Task 4: Row-independent leaves evaluated client-side (shared with SetOps and Misc)

**Flips:** `New_date_time_in_anonymous_type_works`, `Select_anonymous_empty`, `Select_bool_closure`,
`Using_enumerable_parameter_in_projection`, and Misc `MemberInitExpression_NewExpression_is_funcletized_even_when_bindings_are_not_evaluatable`.

**Files:**
- Modify: `NativeProjectionBinder.cs` (`TryTranslateLeafCore`, a new last-resort arm; the bare-body arm :251/:273;
  `TryGetProjectionMembers` handling of a MemberInit whose constructor arguments are parameter-free)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (a new
  `HasClientEvaluatedProjectionLeaf`; a sentinel so that an all-client projection does not collapse to
  `Route = WholeEntity`, :1090)
- Modify: `MongoQueryableMethodTranslatingExpressionVisitor.cs` (`IsPlainProjectedSelect` :3115ff), plus any alias-resolving
  post-projection operator, so they decline when the flag is set
- Test: a new `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeClientEvaluatedLeafTests.cs`

**Interfaces:**
- Produces: `MongoSelectDefinition.HasClientEvaluatedProjectionLeaf` (bool, set only in the binder's commit block).
  **SetOps Task 2 reads it**: a set operation over such a select must decline.
- Produces: `NativeProjectionBinder.IsRowIndependentLeaf(Expression leaf, ParameterExpression selectorParameter)`.

- [ ] **Step 1: Failing tests.**
  - `NativeAndParity` for:
    - `new { A = new DateTime() }`
    - `new { }`
    - a captured anonymous-type closure
    - a captured `List<T>`
    - `new Dto(param) { Id = x.Id, Nested = new Dto(param) }`
  - `DeclinesCleanly` for each of:
    - `.Select(x => new { f = closure }).Union(…)`
    - `.Select(x => new { f = closure, x.Id }).Where(p => p.f)`
    - `.OrderBy(p => p.f)` after the same Select
  - One `Distinct` test: `new { f = closure }` followed by `.Distinct()` returns one row. That is correct, because the
    value is the same for every row in one execution.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement `IsRowIndependentLeaf`.**
  - It returns true when the subtree does not reference the selector parameter and contains only `Constant`,
    `QueryParameterExpression`, `New`, `MemberInit`, `NewArrayInit`, `ListInit`, `Convert` or member-of-constant nodes.
  - It returns false if the subtree has any query root, subquery or other extension node.
  - Admit it in `TryTranslateLeafCore` **only after** the existing `$literal` path declined. This keeps the MQL of
    `Select_anonymous_literal`, `Select_constant_int` and similar tests unchanged. The shaper already evaluates Constant and
    QueryParameter nodes (MPBEV:163/175), so the leaf stays on the shaper and nothing is projected for it.
- [ ] **Step 4: All-client projection.** When every leaf is client-evaluated (`new {}`, a bare closure), stage a
  sentinel projection (`_id: 1` or a `$literal`) so that Route stays `Projection` and the row count is preserved.
- [ ] **Step 5: MemberInit with parameter-only constructor arguments.** Admit it in the member extraction. The bindings
  that depend on the parameter are projected as usual; the constructor arguments and parameter-only bindings stay on the shaper.
- [ ] **Step 6: Set flag and declines.** Set `HasClientEvaluatedProjectionLeaf` in the commit block. Add
  `&& !mongo.Select.HasClientEvaluatedProjectionLeaf` to `IsPlainProjectedSelect`. Make the post-projection `Where`,
  `OrderBy` and GroupBy alias resolution decline when the resolved alias is client-evaluated.
  - **Tell the SetOps agent**: its Task 2 constant-on-source1 rebind must not apply when this flag is set.
- [ ] **Step 7: Run and confirm they pass.** Mutation: drop the `IsPlainProjectedSelect` clause and the Union decline test
  fails (wrong dedup or missing field).
- [ ] **Step 8: Baselines and commit.** `EF-322: native row-independent projection leaves evaluated client-side`.

### Task 5: Client construction over a whole entity

**Flips:** `Projection_of_entity_type_into_object_array`, `…_into_object_list`,
`Projection_with_parameterized_constructor_with_member_assignment`.

**Files:**
- Modify: `NativeProjectionBinder.IsClientOnlyWholeEntitySubtree` (NPB:925; the `sawOpaqueCall` requirement at :922)
- Modify: `MongoShapedQueryCompilingExpressionVisitor.IsCtorWrappedEntityShaper` /
  `IsClientOnlyWholeEntityShaperExpression` (MSQCEV:376/394). Make them call the same predicate as the binder; today they
  are hand-mirrored.
- Test: `NativeCtorOnlyProjectionTests.cs` (extend)

- [ ] **Step 1: Failing tests.**
  - `NativeAndParity` over the tracked entity, comparing by key, for:
    - `new object[] { x }`
    - `new List<object> { x }`
    - `new Wrapper(x) { Name = x.Name }`
  - `DeclinesCleanly` / stays on the scalar path: `new[] { x.Id }`. It has no whole-entity operand, so it must **not**
    switch to whole-document fetching.
  - `new object[] { x }.Union(...)` declines (because `HasClientWrappedWholeEntityShaper` is set).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - Add NewArrayInit, ListInit (single-argument `Add`) and MemberInit/New-with-arguments arms.
  - A whole-entity operand inside a construction counts as satisfying the opaque-call requirement.
  - Keep "at least one whole-entity operand" as a hard requirement.
  - Extract one internal predicate and call it from both the binder and the MSQCEV sites.
- [ ] **Step 4: Run and confirm they pass.** The MQL is unchanged, since the driver already fetches whole documents.
- [ ] **Step 5: Mutation.** Drop the whole-entity-operand requirement and the `new[] { x.Id }` control fails.
- [ ] **Step 6: Commit.** `EF-322: native client construction over whole-entity operands`.

### Task 6: Positional containers (`new[] {…}`, `new List<object> {…}`) of scalars and join-scope entities

**Flips:** `Project_to_int_array`, `Project_to_object_array`, `Projection_of_multiple_entity_types_into_object_array`.
**Also fixes a latent crash:** `new KeyValuePair<Customer,string>(c, c.City)` goes native today and throws
`FormatException: Element '_id' does not match any field or property of class Customer`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/ExpressionExtensionMethods.cs` (`TryGetProjectionMembers` :120 and
  `RebuildProjectionMembers`). Add a new **opt-in** `allowContainerElements` parameter. **Do not** reuse
  `allowPositionalConstructorArguments`: the GroupBy and SelectMany result selectors share that flag, and widening it would widen their gates.
- Modify: NPB multi-argument positional arm (:176); MQTEV `BuildPositionalCtorProjectionShaper` /
  `BindPositionalCtorProjectionMember` (:756/782); `NativeJoinScopeProjectionBinder.TryBindProjection` (:86); MQTEV
  `BuildSelectManyResultShaper` (:2811)
- Test: `NativeArrayProjectionTests.cs` (extend) and `NativeJoinScopeNestedProjectionTests.cs` (extend)

- [ ] **Step 1: Failing tests.**
  - `new[] { x.I, x.NullableI }` returns the exact values.
  - `new object[] { x.I, x.NullableI, x.S }`: assert each element's `GetType()`, since boxing must keep the runtime type.
  - Over a join: `new object[] { o, o.Customer }`.
  - The `KeyValuePair<Entity,string>(x, x.S)` case: either `NativeAndParity` or `DeclinesCleanly`. Prefer declining
    on the scalar path, and materializing the entity only on the join path.
- [ ] **Step 2: Run them and confirm they fail.** Confirm the KeyValuePair test crashes with `FormatException` today.
- [ ] **Step 3: Implement the container mode.** Opt in from only the three call sites listed. Each element becomes one
  `_ctorArg<N>` alias, read with its operand's own serializer. Whole-entity elements are excluded from the scalar path,
  which closes the latent crash.
- [ ] **Step 4: Run and confirm they pass.** Regenerate the two `Project_to_*_array` baselines (`_ctorArg<N>` instead of `_v:[…]`).
- [ ] **Step 5: Mutation.** Remove the whole-entity exclusion and the KeyValuePair test crashes again.
- [ ] **Step 6: Commit.** `EF-322: native positional container projections; decline entity args on the scalar positional path`.

### Task 7: `DateTime.TimeOfDay`

**Flips:** `Select_datetime_TimeOfDay_component`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDatePartExpression.cs` (:20, add `TimeOfDay`)
- Modify: `MongoExpressionTranslator.cs` (:400); `MongoAggregationExpressionRenderer.cs`, which renders
  `{ $dateDiff: { startDate: { $dateTrunc: { date: d, unit: "day" } }, endDate: d, unit: "millisecond" } }`, the driver's shape;
  the gate lists (NPB:719 and `IsArrayFreeComputedSubtree`)
- Modify: `BsonSerializerFactory`, adding a TimeSpan-from-Int64-milliseconds read serializer for this leaf only
- Test: `NativeConditionalAndDateTimeTests.cs` (extend)

- [ ] **Step 1: Failing tests.**
  - `NativeAndParity` for `x.Date.TimeOfDay`, with sub-second values.
  - `x.NullableDate.Value.TimeOfDay` with a null row must throw like C#, not read `00:00`. Assert this against a hand
    oracle. Also check the equivalent `.Value.Year` today, and file a ticket if it has the same hazard.
  - `DateTimeKind.Local` (EF-459) must decline or be proven correct.
- [ ] **Steps 2-4:** Run the tests and confirm they fail, implement, then run and confirm they pass (on server ≥ 5.0,
  since `$dateTrunc` needs it; the container is recent).
- [ ] **Step 5: Mutation, then commit.** Drop the null guard and the null-row test fails. Commit:
  `EF-322: native DateTime.TimeOfDay projection`.

### Task 8: A bare left-outer-join entity null check

**Flips:** `Select_entity_compared_to_null`.

**Files:**
- Modify: `MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`: add an arm beside the conditional join
  arm (:519) using `NativeJoinScopeTranslator.TryMatchScopeNullCheck` (NJST:241). Apply the same left-outer and
  `ForceUnwind` guards as `TryTranslateScopeNullCheckConditional` (NJSPB:378). Project a `MongoLookupNullCheckExpression` under `_v`.
- Test: `NativeJoinScopeConditionalProjectionTests.cs` (extend)

- [ ] **Step 1: Failing tests.**
  - `NativeAndParity` for `Select(o => o.Customer == null)` and `!= null`, over data that includes an order with a
    missing customer and one with a dangling FK.
  - Also run the test under explicit `DriverLinq` (see memory `groupjoin-native-remaining`).
- [ ] **Steps 2-5:** Run and confirm they fail, implement by calling the existing guard predicates without restating
  them, then run and confirm they pass.
  Mutation: drop the left-outer guard and an inner-join variant returns the wrong count.
  Commit: `EF-322: native bare join-scope entity null-check projection`.

### Task 9: A ternary with client-only construction branches

**Flips:** `Projection_custom_type_in_both_sides_of_ternary`.
**Also fixes a latent crash:** the *wrapped* form `new { X = cond ? new P { Id = …, … } : new P { … } }` goes native today
through the NPB:719 `MongoConditionalExpression` arm, which has no subtree check. It then throws
`FormatException: Element 'Id' does not match …`, because native emits `Id` where the class maps it to `_id`.

**Files:**
- Modify: `NativeProjectionBinder.cs`. Stage only the server-translatable test (`c.City == "Seattle"`) as a bool leaf and
  evaluate the conditional client-side, the way the mixed reader's client-computed leaf does. **Do not** add
  `MongoDocumentConstructionExpression` to `IsArrayFreeComputedSubtree` / gate 1c.
- Modify: the NPB:719 `MongoConditionalExpression` admission, which must reject DocumentConstruction branches (the latent crash).
- Test: `NativeDocumentConstructionProjectionTests.cs` (extend)

- [ ] **Step 1: Failing tests.**
  - `NativeAndParity` for the bare ternary, with both branches as constant constructions and with one branch reading `x.Name`.
  - The wrapped latent-crash shape must pass or decline cleanly. It must not throw `FormatException`.
- [ ] **Steps 2-5:** Run and confirm they fail, implement, run and confirm they pass. Mutation: re-admit DocumentConstruction
  in the :719 arm and the wrapped test crashes.
  Commit: `EF-322: native ternary over client-only construction branches; close wrapped-ternary crash`.

### Task 10: Full verification, docs and handoff

- [ ] **Step 1: Run on all three versions.** For each of EF8, EF9 and EF10, build and run:
  - the three suites under NativeOnly;
  - the **full** spec suite in default mode (memory `ef436-groupjoin-and-scope-verification`: per-class verification
    missed a regression);
  - the full FunctionalTests project in default mode.
  Expected result: 0 Select NativeOnly failures on every version. Record the before and after counts from a
  re-derived table, not restated (memory `rederive-counts-never-restate`).
- [ ] **Step 2: Update docs.**
  - Record the new supported shapes and the two closed crashes in `docs/native-query-status-EF-322.md`.
  - Update `src/…/Query/AGENTS.md` if a new invariant was introduced: the shared container-mode opt-in, and the
    `HasClientEvaluatedProjectionLeaf` rule.
- [ ] **Step 3: Final review.** Run `/review-ef-core-provider` on the branch diff and fix Critical/Important findings.
- [ ] **Step 4: Commit.** Commit the docs, then stop. Do not push or squash; ask the owner.
