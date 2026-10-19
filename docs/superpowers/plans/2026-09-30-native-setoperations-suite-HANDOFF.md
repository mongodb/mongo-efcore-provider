# HANDOFF: NorthwindSetOperationsQueryMongoTest, every test native (or failing on both paths)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every `NorthwindSetOperationsQueryMongoTest` test passes under `MONGODB_EF_NATIVE_ONLY=1` on EF8, EF9 and EF10,
or is marked `// Fails:` because it fails on **both** native and driver-LINQ. Today 22 tests (44 results) pass only
through the driver-LINQ fallback on EF10. The EF8 and EF9 sets are identical.

**Architecture:** Every failure throws "Query projects a non-entity result" at
`MongoShapedQueryCompilingExpressionVisitor.cs:317`. The triage traced every `TryTranslateSetOperation` call and found
four causes:
- **(N)** `-o.OrderID` does not bind as a projection leaf at all. That is a binder gap, **owned by the Select plan**
  (see Prerequisite).
- **(A)** The set-op gate requires both sides to have the same alias. A column side is aliased `_id` and a computed
  side `_v`.
- **(C)** The gate rejects a constant or parameter leaf on source1, because source1's shaper would bake the constant
  into every combined row.
- **(J)** The gate rejects a join-scope source1, and the operand lowerer drops the join's post-ops.

This plan does A, C and J. The N-dependent tests flip when the Select plan's Task 1 is merged in.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit (plain `Assert.*`),
Docker/TestContainers.

**Spec:** No separate spec doc. The design comes from the 2026-09-30 native-only triage (Background). Sibling plans:
`docs/superpowers/plans/2026-09-30-native-select-suite.md` (executed by the owner's main session) and
`2026-09-30-native-misc-low-hanging-HANDOFF.md`.

## Where you work

- **Do not work in `/Users/arthur.vickers/code/mongo-efcore-provider`.** Another session is committing to `EF-322c` there.
  Use the clone the owner assigns you (one of `~/code/provider2` … `provider6`), and check `git status` is clean
  first.
- Create a local branch from the main clone's `EF-322c`:
  ```bash
  git fetch /Users/arthur.vickers/code/mongo-efcore-provider EF-322c:EF-322c-setops
  git switch EF-322c-setops
  git log --oneline -1   # expect 8c5f00e1 or later
  ```
- Commit on `EF-322c-setops`. **Do NOT push, and do NOT touch `EF-322c` in the main clone.** The owner integrates.
- No worktrees (owner preference).

## Prerequisite and cross-plan ownership

| Shared capability | Owner | What this plan does |
|---|---|---|
| **Negate leaf, emit and read side** | **Select plan, Task 1** | Do **not** implement it. Tasks 1-3 below do not need it. Task 4 brings it in, either by `git fetch … EF-322c` then `git rebase EF-322c`, or by cherry-picking the Select Task 1 commit (ask the owner for the hash). That flips 10 tests with no further code change. |
| **Client-evaluated projection leaves** (closures, closed `new`, `new {}`) | **Select plan, Task 4** | It adds `MongoSelectDefinition.HasClientEvaluatedProjectionLeaf`, and `IsPlainProjectedSelect` declines it. Your Task 2 handles only `$literal`-rendered constants and parameters, which are **server-side**. Never apply the Task 2 rebind to a client-evaluated leaf. If Select Task 4 has not landed when you do Task 2, gate your rebind on `p.Expression is MongoConstantExpression or MongoParameterExpression` **and** a successful `NativeSlotPopulator.TryProbeBareValueRenders`. That test is exactly the one Select Task 4 uses to separate the two kinds of leaf. |
| **Baselines changed by Select Task 1** | Select plan | Select Task 1 regenerates the ~10 `Union_over_*unary*` baselines in `NorthwindSetOperationsQueryMongoTest.cs` on `EF-322c`. When you rebase you will conflict in that file. Resolve by taking either side and **regenerating** the affected baselines. Never hand-merge MQL. |

## Background (measured 2026-09-30 at `8c5f00e1`, EF10)

A private copy had every fix prototyped, run under NativeOnly, default and explicit DriverLinq, and was then discarded.
Evidence is in
`/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/9fb445c5-259a-41aa-bdf0-74151f361969/scratchpad/triage-setops/`
(`trace.txt`, `v5-native.trx`, `v5-driverlinq.trx`).
MQTEV = `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`.

**How each operand is aliased** (`NativeProjectionBinder.TryBindAsBareProjection`, `:264-303`):
- **column** `o.OrderID` gets `_id` (the DocumentPath tier).
- **binary** `o.OrderID+1`, **function** `GroupBy(o=>o.OrderID).Select(g=>g.Count())`, **scalarsubquery**
  `o.OrderDetails.Count()` and **constant** `8` all get `_v`.
- **unary** `-o.OrderID` never binds, so its Route is Fallback. That is bucket N.

**The set-op gate** (MQTEV `:3024-3028`) requires:
- source1 passes `IsPlainProjectedSelect` / `IsPlainDistinctSelect` / `IsPlainGroupBySelect` with
  `allowPreCombineLookups: true`. These require `!IsJoinQuery`.
- `!HasShaperUnsafeConstantLeaf(mongo1)` (`:3167`).
- source2 is plain with no lookups.
- `ProjectionShapesMatch` (`:3173`): the same alias set.

A side that fails any of these falls through to `MarkNotNativelyRepresentable()` at `:3044`. That explains which
pairs pass today: `column_column` and every `_v`/`_v` pair with no constant on the left. It also explains
`*_scalarsubquery` (a scalar subquery on the **right**): source2 has lookups, and driver-LINQ fails too. Those
overrides already assert failure correctly, and **no fix here changes them**.

**Driver-LINQ is correct for all 22** (default passes, and no override asserts a failure), so **none qualifies for
`// Fails:`**.

| Test | Bucket(s) | Notes |
|---|---|---|
| Union_over_column_function / _column_constant / _column_binary / _function_column / _binary_column / _scalarsubquery_column | A | 6 |
| Union_over_constant_function / _constant_constant / _constant_binary / Union_over_columns_with_different_nullability | C | 4. The last is a Concat of `"NonNullableConstant"` and `(string)null`. |
| Union_over_constant_column | C+A | |
| Union_over_column_unary / Union_over_unary_column | A+N | Flip in Task 4 |
| Union_over_constant_unary | C+N | Flips in Task 4 |
| Union_over_unary_unary / _unary_binary / _unary_constant / _unary_function / _binary_unary / _function_unary / _scalarsubquery_unary | N | 7. They flip in Task 4 with no code here. |
| Concat_with_one_side_being_GroupBy_aggregate (really a Union) | J | source1 `Where(c => c.Customer.City == "Seatte")` becomes a LeftJoin |

Check: 6 + 4 + 1 + 2 + 1 + 7 + 1 = **22**.

## Global Constraints

- Branch `EF-322c-setops` in your assigned clone. Commit messages start with `EF-322: `. **Do NOT push.**
- Preserve file BOMs. `src/` is nullable-enabled.
- Must compile and pass under `Debug EF8`, `Debug EF9` and `Debug EF10`.
- xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset, so each `dotnet test` process gets its own container.
  Never enable test parallelization.
- A recognizer must not mutate and then decline. Every rewrite (re-alias, shaper rebind) happens **inside the admitted
  branch, after every check has passed**.
- The gate and the rewrite call **one shared predicate**. This repo keeps finding the bug where a gate admits more than the fix reaches.
- Every new native shape gets a functional test using `NativeModeAssert.NativeAndParity`
  (`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/NativeModeAssert.cs`). Extend
  `tests/…/FunctionalTests/Query/NativeCombinationQueryTests.cs`, or add `NativeSetOperationProjectionTests.cs` in the
  same style (see `NativeNegationTests.cs` for the seed/context pattern). New native shapes must also be checked under
  explicit `MongoQueryMode.DriverLinq`, which `NativeAndParity` does.
- Guards must be proven by mutation (disable the guard → a named test fails). Record the result in the commit message.
- Stage only files you changed. Never stage `nuget.config`, which is tracked; keep only local modifications uncommitted.
- Scratch logs go in a unique subdirectory of **your own** session scratchpad, one per task. Never share log paths with other agents.
- Regenerate MQL baselines only with a tight `--filter`
  (`EF_TEST_REWRITE_BASELINES=1`, see `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`). Then `git diff`,
  rebuild and rerun without the variable.

```bash
P=tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj
FP=tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test $P -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
dotnet test $P -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"   # default mode
```

## Review Focus

1. **Re-aliasing an operand whose single projection is a DateTime.** `NativeDateTimeKindReadBack` compares the operands'
   kind shapes, so the re-alias must run before it. A `Local`-kind value must read back with source1's kind rule. Pinned
   in Task 1.
2. **A constant on source1 whose BSON round-trip changes the value** (a DateTime or DateTimeOffset kind, Guid
   representation, an enum stored as a string, char). It must come back equal to the C# constant, or decline. Pinned in Task 2.
3. **Union vs Concat dedup when a constant equals a column value.** For example, `Select(o => 10248).Union(Select(o => o.OrderID))`
   must contain 10248 once, and Concat must keep both. Pinned in Task 2.
4. **An anonymous or DTO projection with different member names or orders on each side.** `ProjectionShapesMatch` must stay
   strict for these; only bare scalars re-alias. Pinned in Task 1.
5. **A join-scope source1 whose inner-side filter really matches rows.** The spec's "Seatte" typo always gives an empty
   left side, so a dropped `$match` would still pass. Pinned in Task 3 with a matching city.

---

### Task 1 (A): Bare-scalar operand alias alignment

**Flips:** Union_over_column_function, _column_constant, _column_binary, _function_column, _binary_column and
_scalarsubquery_column. Also enables the A half of column_unary, unary_column and constant_column.

**Files:**
- Modify: MQTEV `TryTranslateSetOperation` (admitted branch, `:3024-3036`) and `ProjectionShapesMatch` (`:3173-3196`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOperationProjectionTests.cs` (new, or extend `NativeCombinationQueryTests.cs`)
- Baselines: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs`

**Interfaces:**
- Produces: `private static bool IsBareScalarOperand(ShapedQueryExpression source, MongoQueryExpression mongo)`. It is
  true when `mongo.Select.Projection.Count == 1` and the shaper is **not** a `NewExpression`/`MemberInitExpression`.
  Detect this by shape. `IsBareProjection` is not set for a grouped `g => g.Count()`.
- Produces: `private static bool CanAlignBareScalarAliases(ShapedQueryExpression s1, MongoQueryExpression m1, ShapedQueryExpression s2, MongoQueryExpression m2)`,
  which the gate uses. The rewrite runs only when the gate admitted through it.

- [ ] **Step 1: Write the failing functional tests.** Seed an entity with `int Id` and `int N`, and cover:
  - `Select(x => x.Id).Union(Select(x => x.N + 1))`
  - `Select(x => x.N + 1).Union(Select(x => x.Id))`
  - `Select(x => x.Id).Concat(Select(x => x.N * 2))`, where Concat must keep duplicates
  - column over a `GroupBy(x => x.N).Select(g => g.Count())`
  - a DateTime column union a DateTime computed value (`x.D.AddDays(1)`), with a `Local`-kind case
  - `DeclinesCleanly` control: `Select(x => new { A = x.Id }).Union(Select(x => new { B = x.N }))`. This cannot be built
    with anonymous types, so use two DTOs with different member names through a common base, or skip it and rely on
    `ProjectionShapesMatch` staying strict for `New`/`MemberInit` (then assert it with a unit test on the predicate).

  ```csharp
  [Fact]
  public void Union_of_column_and_computed_scalar_goes_native()
  {
      var collection = Seed(nameof(Union_of_column_and_computed_scalar_goes_native));
      var results = NativeModeAssert.NativeAndParity(mode =>
      {
          using var db = CreateContext(collection, mode);
          return db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => x.N + 1)).OrderBy(v => v).ToList();
      });
      Assert.Equal(new[] { 1, 2, 3, 11, 21 }, results); // hand oracle for the seed; adjust to the seed
  }
  ```
- [ ] **Step 2: Run them and confirm they fail.** Expect `NativeTranslationNotSupportedException` under NativeOnly.
- [ ] **Step 3: Implement.**
  - Replace `ProjectionShapesMatch(...)` in the gate with
    `(ProjectionShapesMatch(...) || CanAlignBareScalarAliases(source1, mongo1, source2, mongo2))`.
  - Inside the admitted branch, **before** `AppendSetOperation`, when the aliases differ: replace mongo2's single
    projection with one that carries mongo1's alias and the same expression and source. Source1's shaper already
    reads source1's alias.
  - `MongoPipelineFactory.RenderProject` (`:307-331`) already renders both `{_id: {$add…}}` and `{_v: "$_id", _id: 0}`.
    A GroupBy operand's flattening `$project` also works (`{_id: "$_v"}`). Confirm that from the MQL of each test.
  - Run the re-alias **before** anything that compares operand projections by alias, including `NativeDateTimeKindReadBack`.
- [ ] **Step 4: Run the tests and confirm they pass.** Also run the spec suite under NativeOnly and expect the 6 A-only
  tests to flip. Regenerate the baselines that differ (expect column_binary, column_constant, column_function and function_column).
- [ ] **Step 5: Mutation.** Make `CanAlignBareScalarAliases` return true without the `Count == 1` / non-New checks, and the
  anonymous-type control (or the predicate unit test) must fail. Restore it.
- [ ] **Step 6: Run the rest in default mode.** Run the SetOps suite and `NorthwindGroupByQueryMongoTest` (which has
  Union-over-GroupBy tests) in default mode. Any MQL diff must be explained.
- [ ] **Step 7: Commit.** Message: `EF-322: native Union/Concat of bare scalar operands with different aliases`.

### Task 2 (C): A server-rendered constant or parameter leaf on source1

**Flips:** Union_over_constant_function, _constant_constant, _constant_binary,
Union_over_columns_with_different_nullability, and (with Task 1) Union_over_constant_column.

**Files:**
- Modify: MQTEV `TryTranslateSetOperation` (the gate at `:3026` and the admitted branch) and `HasShaperUnsafeConstantLeaf` (`:3167`)
- Test: the same functional class as Task 1

**Interfaces:**
- Consumes (if Select Task 4 has landed): `MongoSelectDefinition.HasClientEvaluatedProjectionLeaf`, which must be false.
- Produces: `private static bool CanRebindConstantLeafToDocument(ShapedQueryExpression source1, MongoQueryExpression mongo1)`.
  It is true when:
  - source1 is a bare scalar (Task 1's `IsBareScalarOperand`);
  - its single projection is `MongoConstantExpression` or `MongoParameterExpression`;
  - `NativeSlotPopulator.TryProbeBareValueRenders` succeeds;
  - the CLR type is in the round-trip-safe allow-list (Step 3).

- [ ] **Step 1: Write the failing tests.** Cover exact values for each of:
  - `Select(x => 8).Union(Select(x => x.N))`, including a row where `N == 8`, so the Union holds one 8 and the Concat holds two
  - `Select(x => "A").Concat(Select(x => (string)null))`
  - `Select(x => p).Union(Select(x => x.N))` with `int p` captured, so it is a parameter leaf
  - `DeclinesCleanly` for a constant `DateTime`, a `Guid` and an enum constant on source1, unless Step 3 proves they round-trip
  - `DeclinesCleanly` for `Select(x => new { A = 8, x.N }).Union(…)`: a constant inside an anonymous type stays out of scope
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - In the gate, replace `!HasShaperUnsafeConstantLeaf(mongo1)` with
    `(!HasShaperUnsafeConstantLeaf(mongo1) || CanRebindConstantLeafToDocument(source1, mongo1))`.
  - In the admitted branch, when the rebind applies, point source1's shaper at the document instead of the baked-in constant:

    ```csharp
    source1 = source1.UpdateShaperExpression(
        BindSelectManyMember(mongo1, mongo1.Select.Projection[0].Alias, source1.ShaperExpression));
    ```

    This is the triage prototype; confirm that `BindSelectManyMember` is the right binder for a scalar alias read, and
    if it isn't, use the one Task 1's column side goes through.
  - The allow-list covers what the prototype proved: `int`, `long`, `double`, `bool`, `string` and `null` string. Add
    `decimal`, `Guid`, enums and `DateTime` only after writing their round-trip test.
- [ ] **Step 4: Run and confirm they pass.** Regenerate baselines: constant_function and
  columns_with_different_nullability (it gets a `$literal` wrap).
- [ ] **Step 5: Mutation.** Skip the shaper rebind while still admitting, and `Select(x => 8).Union(Select(x => x.N))` must
  return only 8s. That is the wrong-data trap this guard exists for. Restore it.
- [ ] **Step 6: Commit.** Message: `EF-322: native set operations with a constant/parameter bare operand on source1`.

### Task 3 (J): A join-scope source1 in a projected set operation

**Flips:** Concat_with_one_side_being_GroupBy_aggregate.

**Files:**
- Modify: MQTEV `IsPlainProjectedSelect` (the `!mongo.IsJoinQuery` at `:3124`). Admit it only when
  `allowPreCombineLookups` holds **and** the select is a confirmed single-level join. Reuse the existing
  `IsSingleEligibleNativeJoinScope` / `HasConfirmedJoinLookup` predicates; do not restate them.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, the OperandsProjected
  branch (`:84-108`). After `AppendLookupStages`, emit `PostJoinOps` and `PostLookupPagingOps`, which today are emitted
  only when `SetOperation == null` (`:65-76`).
- Test: `NativeCombinationQueryTests.cs` or `NativeGroupByOverJoinTests.cs` (extend)

- [ ] **Step 1: Write the failing tests.**
  - `Orders.Where(o => o.Customer.City == "<a city that matches>").Select(o => new { o.OrderDate }).Union(Orders.GroupBy(...).Select(...))`,
    with a hand oracle and `NativeAndParity`.
  - The same with a city that matches nothing.
  - The same with `Skip`/`Take` on the join side.
  - **source2** as a join must still `DeclinesCleanly`, because operand lowering has no lookup plumbing.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement both halves together.** Without the lowerer half, the inner-side `$match` is silently dropped
  and extra rows come back. The test with the matching city is what catches that.
- [ ] **Step 4: Run and confirm they pass.** Run `NorthwindJoinQueryMongoTest` and `NorthwindGroupByQueryMongoTest` in
  default mode and check for no data regressions.
- [ ] **Step 5: Mutation.** Remove the lowerer's `PostJoinOps` emission and the matching-city test must fail with extra rows.
- [ ] **Step 6: Commit.** Message: `EF-322: native projected set operation over a join-scope source1`.

### Task 4: Bring in Negate (Select Task 1), regenerate baselines, verify everything

- [ ] **Step 1: Pull in the Negate commit.** Ask the owner whether Select Task 1 has landed on `EF-322c`, then run
  `git fetch /Users/arthur.vickers/code/mongo-efcore-provider EF-322c && git rebase EF-322c`.
  - Resolve conflicts in `NorthwindSetOperationsQueryMongoTest.cs` by regenerating baselines, never by hand-merging.
  - If Select Task 1 has not landed, **stop and report**. Do not implement Negate here.
- [ ] **Step 2: Remeasure and regenerate.** Run the suite under NativeOnly on EF10 and expect **0 failures**. Regenerate
  every baseline that differs in default mode. The triage predicted 9 churners after N, A and C: column_binary,
  column_constant, column_function, column_unary, columns_with_different_nullability, constant_function,
  function_column, function_unary and unary_function.
- [ ] **Step 3: Full-suite verification on each version.** For each of EF8, EF9 and EF10:
  - the SetOps suite under NativeOnly;
  - the **full** spec suite in default mode;
  - the full FunctionalTests project in default mode.
  Per-class verification has missed regressions before. Report a re-derived before/after count table for each version.
- [ ] **Step 4: Update docs.** Add the supported set-operation operand shapes to `docs/native-query-status-EF-322.md`.
  If the gate's shape changed materially, add a line to `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`.
- [ ] **Step 5: Review and hand back.** Run `/review-ef-core-provider` and fix Critical/Important findings. Commit, then
  report to the owner the branch name, the commit list, the count tables, any `// Fails:` added (there should be none)
  and any follow-ups. **Do not push or merge.**

## Out of scope

- `*_scalarsubquery` on the right side (source2 with lookups). Driver-LINQ fails too, and the existing failure overrides are correct.
- Constants inside anonymous or DTO projections on source1.
- A join-scope **source2**.
