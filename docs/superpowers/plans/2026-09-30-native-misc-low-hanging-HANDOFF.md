# HANDOFF: NorthwindMiscellaneousQueryMongoTest, low-hanging native fallbacks

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the cheap `NorthwindMiscellaneousQueryMongoTest` native-only failures. Each target test passes under
`MONGODB_EF_NATIVE_ONLY=1` on every EF version where it exists, or asserts a translation failure there with an
EF-conformance reason. The expensive buckets are explicitly **out of scope** (listed at the end).

**Architecture:** Five independent changes. Two are translator arms (bool `&`/`|`; regex operators over a computed
string receiver), one is member resolution (the bare lambda parameter after a computed-key `Distinct`), one is a
`TranslateSelect` arm (a parameter-free body over a join), and one is test-only (the `Random.Next` overrides on
EF8/EF9).

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit (plain `Assert.*`),
Docker/TestContainers.

**Spec:** No separate spec doc. The design comes from the 2026-09-30 native-only triage (Background). Sibling plans:
`docs/superpowers/plans/2026-09-30-native-select-suite.md` (executed by the owner's main session) and
`2026-09-30-native-setoperations-suite-HANDOFF.md`.

## Where you work

- **Do not work in `/Users/arthur.vickers/code/mongo-efcore-provider`.** Another session is committing to `EF-322c`
  there. Use the clone the owner assigns you (one of `~/code/provider2` … `provider6`), and check that `git status` is
  clean first.
- Create a local branch from the main clone:
  ```bash
  git fetch /Users/arthur.vickers/code/mongo-efcore-provider EF-322c:EF-322c-misc
  git switch EF-322c-misc
  git log --oneline -1   # expect 8c5f00e1 or later
  ```
- Commit on `EF-322c-misc`. **Do NOT push, and do NOT touch `EF-322c` in the main clone.** The owner integrates.
  Do not use worktrees.

## Cross-plan ownership (Misc tests you must NOT touch)

| Misc test | Owner | Why |
|---|---|---|
| `Non_nullable_property_through_optional_navigation` | **Select plan, Task 3** | It shares the `NativeProjectionBinder.cs:523` null-propagation guard with a Select test. Select Task 3 changes that guard *and* rewrites this override. The triage's cheap alternative (an `IsNativeOnly` branch on `AssertMql`) would be undone by that, so do not apply it. |
| `MemberInitExpression_NewExpression_is_funcletized_even_when_bindings_are_not_evaluatable` | **Select plan, Task 4** | It is a row-independent (parameter-only) constructor argument, the same capability as Select's closure/constant leaves. |
| All MQL baselines outside the tests you flip | nobody | If a default-mode run shows Misc baselines changing that you didn't cause, they come from a Select/SetOps commit you rebased onto. Regenerate them; don't debug them. |

Select Task 2 was deliberately made *narrow* (a PK-never-null check only in `MayBeNullBehindNonNullableType`), so it
does **not** change the roughly 17 Misc baselines that the wide form would.

## Background (measured 2026-09-30 at `8c5f00e1`)

The failures were traced by instrumenting a private copy, which was then discarded. Evidence is under
`/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/9fb445c5-259a-41aa-bdf0-74151f361969/scratchpad/triage-misc/`
(`out10/r2.trx`, `out8/r.trx`, and `up/misc{8,9,10}.cs`, which hold the upstream bodies per release). Default mode is
green on all versions.

**Native-only failures by version (unique tests):**

| Version | Calculation | Misc tests failing native-only |
|---|---|---|
| EF10 | — | 11 |
| EF8 | 11 − 3 + (6 Random + 10 bool bitwise + 3 integer bitwise) | 27 |
| EF9 | 11 − 3 + (6 Random + 10 bool bitwise + 4 integer bitwise) | 28 |

- **EF10-only tests (the −3):** the three `Ternary_*` tests exist only in EF10 upstream (overrides in the `#else` arm,
  `NorthwindMiscellaneousQueryMongoTest.cs:258-296`).
- **EF8/EF9-only tests:** the `*_bitwise_*` and `Random_next_*` tests exist only under `#if EF8 || EF9`: bitwise at
  `:2387-2546`, Random at `:4641-4722`. EF10 moved them to translation suites the provider doesn't run.
- **EF9 has one more:** `Where_bitwise_binary_xor` (at `:2508`, under `#if EF9`).

| Test (versions) | Decline site | Task / disposition |
|---|---|---|
| Select_bitwise_or, _or_multiple, _and, _and_or, _or_with_logical_or, _and_with_logical_and; Where_bitwise_or_with_logical_or, _and_with_logical_and, _or_with_logical_and, _and_with_logical_or (EF8/9) | `MongoExpressionTranslator.cs:584-600` (`TranslateNode`) and `:1757` (value hand-off) know only AndAlso/OrElse. Bool `&`/`\|` are `ExpressionType.And`/`Or`. | **Task 1** (10 tests) |
| Random_next_is_not_funcletized_1..6 (EF8/9) | No `Random.Next` in the translator (`NativeSlotPopulator.cs:290`). The driver passes only by evaluating `Random` client-side, once, at translation time. | **Task 2**, test-only (6 tests) |
| Select_expression_references_are_updated_correctly_with_subquery (all) | `Select(o => o.OrderDate.Value.Year).Distinct().Where(x => x < nextYear)`. The bare parameter `x` doesn't resolve: `TryResolveMember` (`MongoExpressionTranslator.Members.cs:69-70`) handles only a field-backed Distinct key, and `TryResolveFlattenedAlias` (`:171`) handles only a `MemberExpression`. | **Task 3** |
| Join_Customers_Orders_Skip_Take_followed_by_constant_projection (all) | The join is unconfirmed (`MongoSelectDefinition.cs:1081`). The bare-value join arm (MQTEV `:530-545`) fails because `NativeJoinScopeTranslator.cs:393` needs an Outer/Inner access and `"Foo"` has none. | **Task 4** |
| Ternary_Not_Null_Contains, Ternary_Not_Null_endsWith_Non_Numeric_First_Part, Ternary_Null_StartsWith (EF10) | The regex arm at `MongoExpressionTranslator.cs:887-912` requires a field or flattened-alias receiver, but nav-expansion makes the receiver a `$cond`/`$concat`. | **Task 5** |
| Query_expression_with_to_string_and_contains (all) | Two causes: (a) `EmployeeID` is `uint?`, and `TryMatchIntegralToString` (`…MethodCalls.cs:786`) handles only int/long/short/byte; (b) the same computed-receiver `Contains` as above. | **Task 5** |
| Where_bitwise_binary_and, _or, _not (+ _xor on EF9) (EF8/9) | No integer bitwise operators exist. | Out of scope (M) |
| Perform_identity_resolution_reuses_same_instances_across_joins ×4 (all) | Multi-scope whole-entity leaves, a nested Include and identity resolution. | Out of scope (L) |
| Select_expression_other_to_string (all) | `DateTime.ToString()` is declined on purpose. The driver's `$toString` gives ISO format, not .NET's, and passes only because the asserter ignores ShipName. | Out of scope (needs an owner ruling) |
| Select_DTO_distinct_translated_to_server (all) | Distinct over a projection with no server-side leaves; it needs Select Task 4 first. | Out of scope |
| Non_nullable_property_…, MemberInitExpression_… | — | Select plan |

**Result if every task here lands** (Task 5 flips only 1 test on EF8/EF9, because the ternaries don't exist there):

| Version | Calculation | Left after this plan | Also fixed by the Select plan | Genuinely left (out of scope) |
|---|---|---|---|---|
| EF10 | 11 − (1 + 1 + 4) | 5 | 2 | 3 |
| EF8 | 27 − (10 + 6 + 1 + 1 + 1) | 8 | 2 | 6, of which 3 are integer bitwise |
| EF9 | 28 − (10 + 6 + 1 + 1 + 1) | 9 | 2 | 7, of which 4 are integer bitwise |

Re-derive these at the end from the TRX files.

## Global Constraints

- Branch `EF-322c-misc` in your assigned clone. Commit messages start with `EF-322: `. **Do NOT push.**
- Preserve file BOMs. `src/` is nullable-enabled.
- Must compile and pass under `Debug EF8`, `Debug EF9` and `Debug EF10`. Tasks 1 and 2 are about tests that only exist
  on EF8/EF9, but the source change in Task 1 is version-independent, so it must not break EF10. Only the tests are
  under `#if`.
- xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset. Never enable test parallelization.
- A recognizer must not mutate and then decline. The gate and the reader/renderer call **one shared predicate**.
- Every new native shape gets a functional test using `NativeModeAssert.NativeAndParity`
  (`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/NativeModeAssert.cs`; see `Query/NativeNegationTests.cs`
  for the seed/context pattern). Where driver-LINQ is wrong, assert NativeOnly against a hand oracle and say why.
- Guards must be proven by mutation. Record the result in the commit message.
- Stage only files you changed. Never stage `nuget.config`.
- Scratch logs go in a unique subdirectory of **your own** session scratchpad, one per task.
- Regenerate MQL baselines only with a tight `--filter` (`EF_TEST_REWRITE_BASELINES=1`; see
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`). Then `git diff` and rerun without the variable.

```bash
P=tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj
FP=tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
MONGODB_EF_NATIVE_ONLY=1 dotnet test $P -c "Debug EF8" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest"
```

## Review Focus

1. **`bool?` operands of `&`/`|`.** Three-valued `null | true` is `true`, and `null & false` is `false`. That differs from
   AndAlso/OrElse over `$ifNull`-style truthiness. These must decline. Pinned in Task 1.
2. **Value-converted bools** (a bool stored as `"Y"`/`"N"`, or as an int). They must go through the same
   `IsUnsafeTruthinessRoot` gating as `&&`/`||`. Pinned in Task 1.
3. **A regex operator over a computed receiver that evaluates to null.** C# throws `NullReferenceException` on
   `null.StartsWith(..)`, and native must not return a surprising match. It is acceptable to match the existing
   field-receiver behavior, but pin it. The *query-dialect* renderer must never be handed a computed `Field`, because
   it would render a bogus `{ path: /re/ }` that silently matches nothing. Pinned in Task 5.
4. **The bare parameter after `Distinct` when a prior `GroupBy…Select(aggregate)` exists.** It must not bind to the
   wrong alias. Pinned in Task 3.
5. **A constant projection over an inner join that drops unmatched rows.** The row count must equal the join's row
   count, not the outer collection's. Pinned in Task 4.

---

### Task 1: Bool `&` and `|` as logical operators (EF8/EF9 tests; version-independent source)

**Flips (EF8/EF9):** the 10 Select/Where `*_bitwise_*` tests listed in the Background table, excluding the
integer `Where_bitwise_binary_*` tests.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateNode`
  `:584-600`, value hand-off `:1757`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeExprComparisonTests.cs` (extend), or a new
  `NativeBoolBitwiseTests.cs`
- Baselines: the EF8/EF9 `#if` block in `NorthwindMiscellaneousQueryMongoTest.cs` (`:2387-2546`)

**Interfaces:**
- Produces: `private static bool IsNonNullableBoolLogical(BinaryExpression node)`. It is true for
  `ExpressionType.And`/`Or` when `node.Type == typeof(bool)` and both operand types are `bool`, never `bool?`.

- [ ] **Step 1: Write the failing tests.**
  - `NativeAndParity` for each of:
    - `Where(x => x.A == 1 | x.B == 2)`
    - `Where(x => x.Flag & x.A > 0)`
    - `Select(x => x.Flag | x.Other)`
    - `Select(x => new { R = (x.A == 1) & (x.B == 2) })`
  - `DeclinesCleanly` for a `bool?` operand: `Where(x => (x.NullableFlag | x.Flag) == true)`.
  - A value-converted bool: it must behave exactly as the `||` version does today (same route, same result).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** In both places, map an `IsNonNullableBoolLogical` node to the **existing**
  `MongoBinaryOperator.AndAlso`/`OrElse` path, so all current gating applies unchanged (truthiness roots, dialect
  classification, the negator):

  ```csharp
  case ExpressionType.And when IsNonNullableBoolLogical(binary):
      return TranslateLogical(binary, MongoBinaryOperator.AndAlso);   // same helper AndAlso uses
  case ExpressionType.Or when IsNonNullableBoolLogical(binary):
      return TranslateLogical(binary, MongoBinaryOperator.OrElse);
  ```

  Use whatever the AndAlso arm calls today; the name above is illustrative. C#'s `&`/`|` evaluate both sides, but
  server-side evaluation has no side effects, so this is exact.
- [ ] **Step 4: Run and confirm they pass.** On EF8 and EF9, run the Misc suite under NativeOnly and expect the 10 to
  flip. Regenerate their baselines on EF8 and EF9 separately. They sit under `#if EF8 || EF9`, so check the rewriter put
  each baseline in the right arm.
- [ ] **Step 5: Mutation.** Drop the `bool?` exclusion and the `bool?` decline test must fail. Restore it.
- [ ] **Step 6: Run EF10.** Run the full Misc and Where suites on EF10 in default mode and confirm no diffs.
- [ ] **Step 7: Commit.** Message: `EF-322: native non-nullable bool & and | as logical operators`.

### Task 2: `Random.Next` overrides assert a translation failure under native-only (test-only, EF8/EF9)

> **Owner ruling (2026-09-30): YES, do this.** Driver-LINQ "passes" these tests only by evaluating `new Random().Next(...)`
> once, client-side, at translation time, which gives exactly the funcletization the test names forbid. EF relational
> (`NorthwindMiscellaneousQueryRelationalTestBase`) and Cosmos both `AssertTranslationFailed` here, so native declining is
> the EF-conformant behavior. Default-mode behavior is unchanged. Do **not** translate to `$rand`: a seeded
> `new Random(15)` can't be honored.

**Flips (EF8/EF9):** Random_next_is_not_funcletized_1..6.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs` (`:4641-4722`)

- [ ] **Step 1: Read the six overrides.** Tests 1-4 currently catch any exception and turn it into `Assert.Fail`
  ("Exception is expected…").
- [ ] **Step 2: Add a native-only branch.** In each override:

  ```csharp
  if (MongoSpecTestHelpers.IsNativeOnly)
  {
      // Fails: native declines Random.Next, which is EF-conformant (relational/Cosmos AssertTranslationFailed);
      // driver-LINQ passes only by funcletizing Random once at translation time. EF-322
      await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(() => base.Random_next_is_not_funcletized_1(async));
      return;
  }
  // existing default-mode body unchanged
  ```
- [ ] **Step 3: Run the tests.** Run EF8 and EF9 under NativeOnly and in default mode; both must be green.
- [ ] **Step 4: Commit.** Message: `EF-322: Random.Next spec overrides assert native translation failure (EF-conformant)`.

### Task 3: The bare lambda parameter after a computed-key `Distinct`

**Flips (all versions):** Select_expression_references_are_updated_correctly_with_subquery.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs`
  (`TryResolveFlattenedAlias` `:171`, mirroring the field-key rule at `:69-70`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeDistinctTests.cs` (extend)

- [ ] **Step 1: Write the failing tests.**
  - `NativeAndParity` for each of:
    - `Select(x => x.Date.Year).Distinct().Where(y => y < 2020)`
    - `Select(x => x.A + x.B).Distinct().Where(v => v > 3)`
    - `Select(x => x.A + x.B).Distinct().OrderBy(v => v)`, if `OrderBy` resolves through the same path; otherwise skip it
  - Wrong-alias control: `GroupBy(x => x.A).Select(g => g.Count()).Distinct().Where(c => c > 1)`. It must resolve to the
    count (`NativeAndParity`) or decline, never read the key.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** In `TryResolveFlattenedAlias`, when `ReferenceEquals(node, SelfParam)` **and** the
  `DistinctAliasScope` has exactly one key part **and** `Accumulators.Count == 0`, return
  `new MongoElementRefExpression(<that part's alias>, node.Type)`. Otherwise fall through as today.
- [ ] **Step 4: Run and confirm they pass.** Regenerate this test's baseline on each version.
- [ ] **Step 5: Mutation.** Drop the `Accumulators.Count == 0` clause, and the wrong-alias control must fail (or its
  now-admitted variant returns wrong data). Record which.
- [ ] **Step 6: Commit.** Message: `EF-322: native bare-parameter predicate over a computed sole Distinct key`.

### Task 4: A parameter-free Select body over a single eligible join

**Flips (all versions):** Join_Customers_Orders_Skip_Take_followed_by_constant_projection.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  (`TranslateSelect`, beside the bare-value join arm `:530-545`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (extend)

- [ ] **Step 1: Write the failing tests.**
  - `NativeAndParity` for each of:
    - `(from c in Customers join o in Orders on c.Id equals o.CustomerId orderby o.Id select new { c.Name, o.Id }).Skip(1).Take(2).Select(e => "Foo")`
    - the same without paging
    - `Select(e => 42)`
  - The seed **must** include a customer with no orders, so the inner join drops them and the count differs from
    `Customers.Count()`.
  - The paged variant must return exactly `Take` rows after the unwind.
  - Also check under explicit `DriverLinq` (`NativeAndParity` covers this).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - The new arm applies when the body references neither `Outer` nor `Inner` and `IsSingleEligibleNativeJoinScope`
    holds (call it; do not restate it).
  - It stages a bare `$literal` projection, reusing `NativeSlotPopulator.TryProbeBareValueRenders`, and calls
    `ConfirmEntireChain` so the `$lookup`/`$unwind` stay.
  - **Do not loosen `NativeJoinScopeTranslator.cs:393`.**
  - Skip/Take must stay after the unwind, the way the existing bare-value arm arranges it.
- [ ] **Step 4: Run and confirm they pass.** Regenerate this test's baseline.
- [ ] **Step 5: Mutation.** Skip `ConfirmEntireChain` or the lookup, and the no-orders-customer test must fail with extra rows.
- [ ] **Step 6: Commit.** Message: `EF-322: native parameter-free projection over a single join scope`.

### Task 5: `StartsWith`/`EndsWith`/`Contains` over a computed string receiver; unsigned `ToString`

This is the borderline low-hanging task, because it touches three dispatch sites. If it grows beyond them, stop after
Step 3 and report.

**Flips:** Ternary_Not_Null_Contains, Ternary_Not_Null_endsWith_Non_Numeric_First_Part and Ternary_Null_StartsWith
(EF10), plus Query_expression_with_to_string_and_contains (all versions).

**Files:**
- Modify: `MongoExpressionTranslator.cs` `:887-912`. Admit a computed receiver (any `TryTranslateValue` result of type
  string) for the regex arm, **aggregation dialect only**.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`. Both `RenderNode`
  (`:62`) and `IsQueryDialectRenderable` (`:419-421`) key only on `Term`. Add a conjunct requiring
  `Field is MongoFieldExpression or MongoElementRefExpression` to both, so that a computed receiver always goes through
  `$expr`/`$regexMatch`.
  - Per `src/…/Query/AGENTS.md`, a shape-conditional dispatch spread across three sites calls for a **sealed sibling
    node type** instead (for example a `MongoComputedRegexExpression`). Prefer that if it is no larger. The node-coverage
    test (`MongoExpressionNodeCoverageTests`) can't see shape-conditional dispatch, so the dedicated tests below are mandatory.
- Check: `MongoExpressionNegator.cs:99` (the regex negation special case) and `MongoFieldPrefixRewriter` must accept the
  computed receiver, or leave it untouched correctly.
- Modify: `…MethodCalls.cs` `:786` (`TryMatchIntegralToString`) to add `uint`, `ushort`, `sbyte` and `ulong`.
  - Their default serializers store values that `$toString` renders exactly. **Verify `ulong` above `long.MaxValue`**;
    decline `ulong` if its storage is not exact.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeFieldToFieldRegexTests.cs` (extend) and
  `NativeClientMethodProjectionTests.cs` or the string-translation tests (extend)

- [ ] **Step 1: Write the failing tests.**
  - `NativeAndParity` for each of:
    - `Where(x => (x.A + "").Contains("1"))`
    - `Where(x => (x.S ?? "z").StartsWith("a"))`
    - `Where(x => !(x.S + "q").EndsWith("q1"))` (the negated form)
    - `OrderBy(x => x.Id).Select(x => x != null ? x.A + "" : null).FirstOrDefault(s => s.Contains("1"))`
      (the spec shape)
    - `Where(x => x.UIntField.ToString().Contains("7"))`
  - Null receiver: `Where(x => (x.NullableS + x.NullableS2).StartsWith("a"))`, with rows where both are null. Pin
    whatever the existing field-receiver behavior is for a null field, and assert that native matches it.
  - A renderer unit test (UnitTests project): a `MongoRegexExpression` with a computed `Field` is **not**
    `IsQueryDialectRenderable`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement the translator arm and both renderer conjuncts (or the sibling node) together, then B2.**
- [ ] **Step 4: Run and confirm they pass.** On EF10, regenerate the three `Ternary_*` baselines (`:264`, `:274`, `:294`),
  which are currently driver-shaped. On all versions, regenerate `Query_expression_with_to_string_and_contains`.
- [ ] **Step 5: Mutation.** Drop the renderer conjunct, and the renderer unit test plus at least one functional test must
  fail (the query-dialect path returns zero rows). Record which.
- [ ] **Step 6: Run the rest in default mode.** Run `NorthwindWhereQueryMongoTest`, `NorthwindFunctionsQueryMongoTest` and
  `Translations/String*` in default mode. Newly native shapes there are expected; regenerate those baselines and list
  them in the commit message.
- [ ] **Step 7: Commit.** Message: `EF-322: native regex operators over computed string receivers; unsigned integral ToString`.

### Task 6: Verify and hand back

- [ ] **Step 1: Rebase.** If the owner says Select/SetOps commits have landed on `EF-322c`, run
  `git fetch /Users/arthur.vickers/code/mongo-efcore-provider EF-322c && git rebase EF-322c`, and resolve baseline
  conflicts by regenerating.
- [ ] **Step 2: Full verification on each version.** For each of EF8, EF9 and EF10:
  - the Misc suite under NativeOnly;
  - the **full** spec suite in default mode;
  - the full FunctionalTests project in default mode.
  Produce a before/after table of Misc native-only failures per version, re-derived from TRX files. Do not restate
  numbers from this plan.
- [ ] **Step 3: Update docs.** Add the supported shapes to `docs/native-query-status-EF-322.md`. Add the new out-of-scope
  items there too, if they aren't listed.
- [ ] **Step 4: Review and report.** Run `/review-ef-core-provider` and fix Critical/Important findings. Commit, then report
  the branch, commits, count tables and follow-ups to the owner. **Do not push or merge.**

## Out of scope (record them in the status doc; do not start them)

- **Integer bitwise operators** (`Where_bitwise_binary_and/_or/_not/_xor`, EF8/EF9). This needs new `$bitAnd`/`$bitOr`/
  `$bitXor`/`$bitNot` nodes threaded through the negator, the dialect classifier, both renderers and the coverage test
  (server 6.3+). Optionally it could use a query-dialect `$bitsAllSet` for `(x & C) == C`. Size M.
- **`Perform_identity_resolution_reuses_same_instances_across_joins`.** Multi-scope whole-entity leaves, a nested Include
  in a projection leaf and identity resolution. Size L.
- **`Select_expression_other_to_string`.** Needs an owner ruling: driver-parity `$toString` (ISO format, not .NET) or a
  client-evaluated leaf.
- **`Select_DTO_distinct_translated_to_server`.** Distinct over a projection with no server-side leaves. Revisit after
  Select Task 4 lands.
