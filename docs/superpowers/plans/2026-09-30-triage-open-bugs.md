# Fix the open EF bugs that still reproduce on EF-322c — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate every silent-wrong-result bug, and the loud failures that are worth fixing, among the open Jira bugs
filed by the owner that still reproduce on `EF-322c` (EF-460, EF-255, EF-217, EF-459, EF-337, EF-247, EF-436, EF-220,
EF-443, EF-228, EF-252).

**Architecture:** Silent bugs first, and each is made *loud or correct* before anything else: decline/throw where a correct
server-side translation isn't cheap (EF-255, EF-217, EF-459, EF-337), fix the reader where the data is already correct
in the projected document (EF-460), then add native breadth for the loud gaps (EF-247, EF-436, EF-220). DriverLinq is
being retired, so DriverLinq-only defects (EF-443, EF-228) get tests/closure, not new machinery. A recognizer must
never mutate then decline (`Query/AGENTS.md`).

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver 3.x, xUnit (plain `Assert.*`),
Docker (Atlas-local containers).

**Spec:** None. Evidence is the triage on 2026-09-30, recorded as comments on EF-255, EF-337, EF-217, EF-459, EF-456,
EF-358; the repro harness is preserved at
`/private/tmp/claude-502/-Users-arthur-vickers-code-provider4/e61fff78-1ca0-4349-b85a-2347f8b6edfe/scratchpad/branch-agent/TriageRepro.cs.bak`
(one probe per bug, both `MongoQueryMode.Native` and `DriverLinq`). Root causes and file:line anchors below come from
read-only code analysis and have **not** been confirmed by running anything — every task therefore starts by writing
the failing test, which is the confirmation.

## Global Constraints

- Work directly on branch `EF-322c` (check `git branch --show-current` first; it can change under a session). **No
  worktrees.** Every commit subject starts `EF-322: ` (other ticket keys in the body). **Do NOT push, force-push or
  rebase** without being asked.
- Preserve file BOMs. `src/` is `<Nullable>enable</Nullable>`.
- Must compile and pass under `Debug EF8`, `Debug EF9`, `Debug EF10`; use `/test-all`.
- Tests: xUnit, plain `Assert.*` (no FluentAssertions); run with `MONGODB_URI` and `ATLAS_URI` **unset**; never enable
  test parallelization. Give any parallel subagent a unique scratch subdir.
- New/changed query shapes MUST be tested under default `Native`, `NativeOnly` where the shape is meant to be native, and
  explicit `DriverLinq` (`Query/AGENTS.md`).
- DateTime tests must not pass by accident of the machine's time zone: assert `Kind` and ticks against entity
  materialization of the same property, and run once under a non-UTC `TZ` (e.g. `TZ=Europe/London`, and
  `TZ=America/New_York`).
- Any change to the native dispatch / allow-list / fallback gate is only "done" after the **full** suite passes on
  EF8/EF9/EF10 (per-test-class verification has missed regressions before).
- A guard test must be proven to discriminate **by mutation**: revert the guard, watch the test fail, restore.
- Jira: comments/transitions are the owner's call. This plan makes **no** Jira changes; tasks end by listing which
  tickets are ready to close, for the owner to action.
- Breaking-change rubric (`AGENTS.md`): which internal path a supported query takes and the exact MQL are not contract;
  turning a silent wrong answer into an exception, or a wrong answer into a right one, is not a break. Record the
  behaviour change for users in `BREAKING-CHANGES.md` only where a *previously working* query starts throwing
  (Task 5, Task 6).

## Decisions made by this plan (recommended defaults — owner may overrule before execution)

| # | Decision | Default taken | Alternative |
|---|---|---|---|
| D1 | EF-217 `DateTimeOffset.ToString()` server-side | Do not render server-side; evaluate the receiver on the client (fallback: throw) | Render ISO-8601 with `$dateToString` (not equal to .NET output) |
| D2 | EF-459 Local-kind date parts | **Decline** on both paths (loud) now; host-`timezone` rendering is a follow-up, not in this plan | Render with host `timezone` (L, needs Windows tz-id mapping) |
| D3 | EF-337 representation/converter properties | Refuse pushdown loudly (sort, relational comparison, fallback aggregates) | `$toInt/$toDecimal` conversions; client-side reduction |
| D4 | EF-255 non-deterministic calls | Throw (`InvalidOperationException`) when the call would be baked into a query | Map `Random.NextDouble` to `$rand` (separate work) |
| D5 | EF-443 / EF-228 DriverLinq-only | Fix EF-443 only if Task 1's sweep makes it cheap, else close won't-fix; EF-228 tests only | Fix both in the bridge |

## Review Focus

- A `Local`-kind property with a non-UTC host zone must never yield a wrong `Hour`/`Date`/`AddDays` on **either** path.
- A nullable / missing member under a retained pushed-down `Select` (`Distinct`, `Union`) must give C# semantics
  (`null`/`""`) or throw, never a silently different value.
- A property with a string `BsonRepresentation` or a value converter must never be silently sorted, compared or
  aggregated as its stored representation.
- Declining a shape must not mutate the query state first (no leaked `__sq` lookup field, no half-registered join).
- `DateTime.UtcNow`/`Now` (parameterized per execution by EF) must keep working after the EF-255 guard.

## File Structure (what changes, by responsibility)

| File | Responsibility | Tasks |
|---|---|---|
| `Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs` | Shaper leaf binding for mixed projections | 1 |
| `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` | EF→driver-LINQ bridge; the place to throw before the driver silently misbehaves | 2, 3, 4, 5 |
| `Query/NativeTranslation/MongoExpressionTranslator*.cs` (+`.MethodCalls.cs`, `.Regex.cs`) | Native predicate/projection translation | 2, 3, 4, 5, 6 |
| `Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`, `MongoQueryLanguageRenderer.cs` | MQL rendering; dialect routing | 6 |
| `Query/NativeTranslation/NativeGroupByBinder.cs` (`HasDefaultKeySerialization`) | Shared "default serialization" predicate | 5 |
| `Query/NativeTranslation/NativeSlotPopulator.cs`, `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | Join/lookup lowering | 7, 8 |
| `Query/NativeTranslation/LookupExpression.cs` | `$lookup` node (`PipelineStages`, `CorrelatedReducer`) | 7, 8 |
| `tests/…FunctionalTests/Query/*` | Regression tests | all |
| `tests/…SpecificationTests/…/NorthwindJoinQueryMongoTest.cs` etc. | Spec baselines | 7, 8 |
| `BREAKING-CHANGES.md` | User-visible behaviour change log | 5 |

---

## Task 1: EF-460 (+ EF-443 sweep) — read the projected alias under a retained pushed-down Select

The described silent bug (wrong values) is latent: with the current required `S`, the read throws; with nullable
`S`/`T` it likely returns `default` silently. Data is already correct in the driver's projected document (`{R, V}`).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`
  (`TryBindClientComputedLeaf` guard ~:447, `TryBindArithmeticLeaf` ~:660, `ResolveArithmeticOperand` ~:700, final alias
  read ~:322-326; `_pushedDownSelectRetained` is set at `MongoShapedQueryCompilingExpressionVisitor.cs:361`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/MixedReaderClientLeafTests.cs` (already seeds labels
  `missing/null/sNull/tNull/value`; existing pin `Computed_leaf_under_a_retained_pushed_down_Select` ~:295)
- Test (EF-443): `tests/…/Query/NativeComputedProjectionTests.cs` (pin at ~:366, Theory at ~:344)

**Interfaces:**
- Consumes: `MixedReaderClientLeafTests` helpers `AssertRows(mode, query, expected[])`, `Show(...)`, `Row` with
  nullable `S`, `T` and owned `O`.
- Produces: none (self-contained).

- [ ] **Step 1: Write the failing tests** — add to `MixedReaderClientLeafTests`:

```csharp
// C# answers per label (missing, null, sNull, tNull, value) — compute from Row's seed, do not hard-code guesses:
// Concat: S + T with null-as-empty; arithmetic: int? + int? -> null when either is null.
[Theory]
[InlineData(nameof(MongoQueryMode.DriverLinq))]
[InlineData(nameof(MongoQueryMode.Native))]
public void Concat_and_arithmetic_leaf_under_a_retained_pushed_down_Select(string mode)
{
    AssertRows(mode, q => q.Select(x => new { R = x.O, V = x.S + x.T }).Distinct()
            .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)),
        expected: ExpectedFromLinqToObjects(rows => rows.Select(x => new { R = x.O, V = x.S + x.T }).Distinct()
            .Select(x => Show(x.R?.W) + ":" + Show(x.V))));
    // same with .Union(...) and with an int? arithmetic leaf (x.I1 + x.I2)
}
```

  Implement `ExpectedFromLinqToObjects` as the oracle (run the same lambda over the in-memory seed list) — the oracle,
  not a literal, is what proves C# semantics for null/missing.

- [ ] **Step 2: Run to confirm they fail**
  `env -u MONGODB_URI -u ATLAS_URI dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MixedReaderClientLeafTests"`
  Expected: FAIL — "Document element is missing for required non-nullable property 'S'" (required) or wrong values
  (nullable). Record which; that confirms the latent silent bug.
- [ ] **Step 3: Implement** — in `MongoMixedProjectionBindingRemovingExpressionVisitor`, when `_pushedDownSelectRetained`
  is true and the leaf is an arithmetic/concat `BinaryExpression`, do **not** enter `TryBindArithmeticLeaf`; fall to the
  final alias read (:322-326) so the projected `V` is read by name. Read an operand's alias only when its name matches a
  projected member with identical source (the discriminator). Method-call leaves (`Substring`, `ToUpper`…) **keep
  declining** (driver `ToUpper`/`ToLower` are ASCII-only). Do not touch the `Substring` pin.
- [ ] **Step 4: Re-run** the class on EF10 — new tests PASS, existing pins unchanged.
- [ ] **Step 5: Mutation check** — revert Step 3's condition; the new tests must FAIL again; restore.
- [ ] **Step 6: EF-443 sweep (timebox 2h)** — add `TryBindCollectionCountLeaf` (read `BsonArray` at the collection
  navigation path from the whole document; missing/null → 0; use `CreateGetBsonArray` / `BsonBinding.TryGetValueAtPath`)
  and add `DriverLinq` to the `NativeComputedProjectionTests` Theory, deleting the ~:366 pin. If the sweep shows other
  leaves (`Sum`/`Any` over owned collections) need more than this one arm, **stop**, revert the EF-443 part, and note
  "close EF-443 won't-fix (DriverLinq retiring)" in the report.
- [ ] **Step 7: Full check** — `/test-all` (EF8/9/10) for `Query` functional tests, then commit:
  ```bash
  git add -A src tests
  git commit -m "EF-322: read projected alias for concat/arithmetic leaves under a retained pushed-down Select" -m "Fixes EF-460 (latent silent wrong values); EF-443 count leaf if included."
  ```

---

## Task 2: EF-255 — never bake a non-deterministic call into a query

**Files:**
- Modify: `…/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (throw before handing the tree to the driver)
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TryEvaluateClosedSubtree` ~:323-345)
- Create: `…/Query/NonDeterministicCalls.cs` (`internal static class NonDeterministicCalls { bool IsNonDeterministic(MethodCallExpression) }`)
- Test: `tests/…FunctionalTests/Query/NonDeterministicFunctionTests.cs` (new)

**Interfaces:**
- Produces: `NonDeterministicCalls.IsNonDeterministic(MethodCallExpression call)` — true for `System.Random.Next*`,
  `Random.Shared.Next*`, `NextDouble`, `Guid.NewGuid`. `DateTime.Now/UtcNow` are property accesses that EF parameterizes
  per execution — they must stay untouched.

- [ ] **Step 1: Write failing tests** (both modes; fixture per `DateTimeOffsetMemberProjectionTests` pattern):

```csharp
[Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
public void Random_in_predicate_throws_rather_than_being_evaluated_once(MongoQueryMode mode)
{
    using var db = CreateContext(mode);
    Assert.Throws<InvalidOperationException>(
        () => db.Set<Order>().Where(o => o.Foo > Random.Shared.Next()).ToList());
}

[Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
public void UtcNow_in_predicate_still_works(MongoQueryMode mode)
{
    using var db = CreateContext(mode);
    Assert.Equal(2, db.Set<Order>().Where(o => o.Created < DateTime.UtcNow).Count());
}
```

  Also a test that `Random` in a **projection** keeps today's behaviour (verify first what it does; pin it — do not
  break it accidentally).
- [ ] **Step 2: Run** — Random test FAILS (no exception, wrong rows silently); UtcNow test passes.
- [ ] **Step 3: Implement** the predicate; call it from `TryEvaluateClosedSubtree` (return false) and from the bridge
  visitor's predicate/ordering/join visits (throw `InvalidOperationException` with a message naming the call and saying
  it cannot be evaluated once per row).
- [ ] **Step 4: Run** — PASS. **Step 5: mutation check** (remove the bridge throw → Random test must fail).
- [ ] **Step 6: Commit** `EF-322: throw instead of evaluating non-deterministic calls once (EF-255)`.

---

## Task 3: EF-217 — stop silently returning BSON JSON for `DateTimeOffset.ToString()`

**Files:**
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (~:780-790 ToString handling)
- Modify: `…/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (near the DateTimeOffset member rewrite
  ~:478-510, `DateTimeOffsetComponentMembers` :50)
- Modify (if client-eval route): `…/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`
  (`ClientPropertyReadRewriter` / client-computed leaf)
- Test: `tests/…FunctionalTests/Query/DateTimeOffsetMemberProjectionTests.cs`

- [ ] **Step 1: Write failing tests** (both modes; the test class already has `TestValue` with a −05:00 offset):

```csharp
[Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
public void Select_DateTimeOffset_ToString_matches_client_evaluation(MongoQueryMode mode)
{
    using var db = CreateContext(mode);
    var result = db.Entities.Select(e => e.DateTimeOffset.ToString()).Single();
    Assert.Equal(TestValue.ToString(), result);           // today: raw {"DateTime":..., "Ticks":..., "Offset":...} JSON
}
```
  plus `OptionalDateTimeOffset` (nullable) and `Where(e => e.DateTimeOffset.ToString() == …)`.
- [ ] **Step 2: Run** — FAIL with the JSON string. **Step 3: Implement (D1)**: make `ToString`/`ToString(format)` on
  `DateTimeOffset` (and `DateTime`) a *client-evaluated leaf*: project the receiver, call `ToString` in the shaper. If
  the client-leaf plumbing is not reachable on the bridge path within a day, fall back to throwing
  `InvalidOperationException` in the bridge (loud > silent) and note it for the owner. A `Where` on it throws (not
  translatable) — assert that.
- [ ] **Step 4: Run** — PASS; **Step 5: mutation check**; **Step 6: Commit**
  `EF-322: DateTimeOffset.ToString() is client-evaluated instead of returning BSON JSON (EF-217)`.

---

## Task 4: EF-459 — Local-kind DateTime date parts decline loudly (D2)

**Files:**
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.cs` (:181-251, :282+ date-part / date-add entry)
- Modify: `…/Query/NativeTranslation/NativeDateTimeKindReadBack.cs` (`HasUnreproducibleReadBack` — already declines
  computed dates for Local; reuse its predicate so the gate and the fix share one predicate)
- Modify: `…/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (throw for a Local-kind property under
  date-part / date-add members — no such check exists today)
- Test: `tests/…FunctionalTests/Query/NativeDateTimeKindTests.cs` (find the existing DateTimeKind read-back tests;
  add there)

- [ ] **Step 1: Write failing tests**, one property `HasDateTimeKind(DateTimeKind.Local)`, value 10:30 Local, in both
  modes, asserting **either the correct answer or an exception, never a wrong answer**:

```csharp
static void AssertCorrectOrLoud<T>(Func<T> query, T expected)
{
    try { Assert.Equal(expected, query()); }
    catch (InvalidOperationException) { /* declining loudly is acceptable */ }
}
// Hour, Where(Hour == 10), Date, AddDays(1), Year/Month/Day; each in Native and DriverLinq.
```
  Run under `TZ=America/New_York` and `TZ=Asia/Tokyo`. Add the same shapes on a `Utc`-kind property and assert they
  **still work** (no over-declining).
- [ ] **Step 2: Run** under a non-UTC TZ — FAIL (Hour 14 vs 10 etc.).
- [ ] **Step 3: Implement** — decline in native via the shared predicate; throw in the bridge. Message names the
  property and says Local-kind server-side date arithmetic isn't supported.
- [ ] **Step 4: Run** both TZs — PASS. **Step 5: mutation check. Step 6: `/test-all`** (dispatch change → full suite).
  **Step 7: Commit** `EF-322: decline server-side date parts on Local-kind DateTime properties (EF-459)`.

---

## Task 5: EF-337 — refuse pushdown over represented/converted properties (D3)

**Files:**
- Modify: `…/Query/NativeTranslation/NativeGroupByBinder.cs` (`HasDefaultKeySerialization` ~:185-193; the stale comment
  at :190 "Accumulator operands aren't checked")
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TryTranslateField` ~:149 has no guard → sort keys;
  relational comparison branch ~:1223-1290 → `>`, `<`, `>=`, `<=`)
- Modify: `…/Query/NativeTranslation/NativeSlotPopulator.cs` (sort)
- Modify: `…/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (throw `NotSupportedException` for aggregate /
  comparison / ordering over such a property — declining only moves it to the buggy driver)
- Test: `tests/…FunctionalTests/Query/BsonRepresentationTests.cs`, `ValueConverterTests.cs` (may need expectation
  changes), new `…/Query/RepresentedPropertyPushdownTests.cs`

**Interfaces:**
- Consumes/Produces: reuse `HasDefaultKeySerialization(IProperty)` as the single predicate for the gate **and** the
  fix (invariant: gate calls the fix's predicate, never restates it).

- [ ] **Step 1: Write failing tests** — int stored `HasBsonRepresentation(BsonType.String)` seeded 9/10/100, plus an
  `int` with a string `ValueConverter`. In both modes assert: `Sum`, `Min`, `Max`, `Average`, `Where(Price > 50)`,
  `OrderBy(Price)` each either equal the LINQ-to-objects oracle or throw `NotSupportedException`/`InvalidOperationException`
  (never wrong). Equality (`Where(Price == 10)`) must keep working (regression guard).
- [ ] **Step 2: Run** — FAIL (Sum 0, Max 9, Min 10, `[1]`, `[2,3,1]`).
- [ ] **Step 3: Implement** the guards. Before throwing, enumerate **currently-working** cases that would now throw
  (e.g. order-preserving converters like DateTime→ticks): run the existing `ValueConverterTests` /
  `BsonRepresentationTests`; for each break, decide allow-list (same ordering class) vs accept-and-document.
- [ ] **Step 4: Run** target tests, then **full suite EF8/9/10**. **Step 5: mutation check** per guard site
  (sort, relational compare, aggregate, bridge).
- [ ] **Step 6: Update `BREAKING-CHANGES.md`** for any previously-working query that now throws.
- [ ] **Step 7: Commit** `EF-322: refuse server-side aggregate/compare/sort over represented or converted properties (EF-337)`.

---

## Task 6: EF-247 — `Regex.IsMatch` with a non-constant pattern (native only)

**Files:**
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.Regex.cs` (`TryTranslateForwardRegexIsMatch` ~:110,
  reversed shape ~:68)
- Modify: `…/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` (`CanRender` Pattern arm ~:241-244; Pattern
  render ~:630 already uses `RenderOperand(regex.Term)`)
- Modify: `…/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (`RenderRegex`, `IsQueryDialectRenderable` — a
  field-term pattern must route to `$expr`; `$regularExpression` needs a literal)
- Modify: `…/Query/NativeTranslation/MongoRegexExpression.cs` (doc)
- Test: `tests/…FunctionalTests/Query/` regex tests (find existing `Regex` test class) + unit tests for CanRender /
  dialect routing (AGENTS.md: `MongoExpressionNodeCoverageTests` is blind to shape-conditional dispatch — needs its own)

- [ ] **Step 1: Write failing tests** — `Where(o => Regex.IsMatch(o.Foo, o.Bar))` under `NativeOnly` with rows including
  null/missing `Bar`; oracle = LINQ-to-objects with null → false. Under explicit `DriverLinq` assert the *current throw*
  (`ExpressionNotSupportedException`) — pins that the driver leg is still Blocked on a driver change.
- [ ] **Step 2: Run** — Native FAIL (falls back and throws). **Step 3: Implement** — non-constant pattern: resolve
  a plain string property via `TryResolveMember` (non-outer) as `MongoFieldExpression` Term (or accept
  `MongoParameterExpression`); relax `CanRender`; route field-term to `$expr`; keep options constant.
- [ ] **Step 4: Run** — PASS. **Step 5: mutation check** (dialect routing: force `$regularExpression` → must fail).
  **Step 6: full suite** (dispatch change). **Step 7: Commit** `EF-322: native Regex.IsMatch with a field pattern (EF-247)`.

---

## Task 7: EF-436 — native composite / anonymous-key `Join` / `GroupJoin`

Only the three `GroupJoin_aggregate_anonymous_key_selectors` variants are a genuine gap; the rest of the ticket is stale
(see Step 7).

**Files:**
- Modify: `…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateJoinCore` ~:2140,
  `JoinLookupImplementsKeySelectors` ~:2245, `TryResolveRawKeyJoinProperties` ~:2344 — both call
  `TryGetSimplePropertyName`, which returns null for `new { … }`)
- Modify: `…/Query/NativeTranslation/LookupExpression.cs` (ctor ~:93; `ToLookupStageDocument` hard-codes a single
  `$eq` — add multi-key `let` variables and `$match {$expr:{$and:[{$eq:["$f","$$k0"]}, …]}}`)
- Reuse: composite-PK path (`_id.<Name>`, `LookupExpression.GetFieldPath`), covered by `NativeCompositeKeyJoinTests`
- Test: `tests/…FunctionalTests/Query/` new `NativeAnonymousKeyJoinTests.cs`; spec:
  `tests/…SpecificationTests/…/NorthwindJoinQueryMongoTest.cs` overrides at ~:58-83

- [ ] **Step 1: Confirm which path each spec test takes** — run the three under `NativeOnly` and read the exception
  (composite-key `Join` decline vs `NativeCorrelationMatcher.TryMatchCorrelatedCollection` decline).
- [ ] **Step 2: Write failing functional tests** — `Join`/`GroupJoin` with `new { a.X, a.Y } equals new { b.X, b.Y }`,
  incl. rows with null/missing key parts (`$eq` on missing vs null differs from C#: assert C# semantics, decline if
  not reproducible). Test under `NativeOnly` **and** explicit `DriverLinq` (assert unchanged failure mode).
- [ ] **Step 3: Implement** multi-key lookup + accept `New`/`MemberInit` bodies of simple properties with matching
  member names/positions. Anything else keeps declining ("non-simple key declines" rule).
- [ ] **Step 4: Run** functional tests; **Step 5: spec** — replace the 3 `AssertTranslationFailed` overrides with real
  MQL baselines (`EF_TEST_REWRITE_BASELINES=1`, then review the diff by hand).
- [ ] **Step 6: mutation check; full suite EF8/9/10** (joins are a dispatch change).
- [ ] **Step 7: Ticket hygiene list** (report to owner, no Jira edits): `GroupJoin_DefaultIfEmpty_multiple` already
  passes (EF-375) — remove from EF-436; `GroupJoin_DefaultIfEmpty2` → EF-X022; `GroupJoin_subquery_projection_outer_mixed`
  → EF-220; `Unflattened_GroupJoin_composed(_2)` / `GroupJoin_on_true_equal_true` → EF Core throws first, by-design
  (verify against upstream relational before relabeling; the `_nested` variant has no Mongo override — check it).
  **Step 8: Commit** `EF-322: native composite/anonymous-key Join and GroupJoin (EF-436)`.

---

## Task 8: EF-220 — uncorrelated scalar subquery (sub-shape A only)

Sub-shape B (cross join over unrelated roots, ~60 spec tests) is **out of scope**: it is L–XL, entangled with
reference-Include work and EF-X022, and needs its own design (`docs/superpowers/specs/…`) reviewed first.

**Files:**
- Modify: `…/Query/NativeTranslation/NativeSlotPopulator.cs` (Where arm ~:180-186 has no `else`)
- Modify: `…/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (recognise an uncorrelated
  `First/FirstOrDefault` chain over an `EntityQueryRootExpression` with `Where/OrderBy/Take`) — or a new
  `NativeUncorrelatedSubqueryBinder.cs`
- Modify: `…/Query/NativeTranslation/LookupExpression.cs` (an "uncorrelated" pipeline kind: no `localField`, no FK `$match`)
- Modify: `…/Query/NativeTranslation/MongoSelectLowerer.cs`, `MongoPipelineFactory.cs` (pre-match `$lookup` + trailing `$unset`)
- Test: `tests/…FunctionalTests/Query/NativeUncorrelatedSubqueryTests.cs` (new); spec
  `NorthwindWhereQueryMongoTest`, `NorthwindMiscellaneousQueryMongoTest` overrides for `Where_shadow_subquery_FirstOrDefault`

- [ ] **Step 0: Design gate.** Because the plan's estimate is M–L and the per-document cost is real, write a short
  design note first (`docs/superpowers/specs/2026-09-30-native-uncorrelated-subquery-design.md`): is the temp-field
  `$lookup` (runs the subquery per outer document) acceptable, or must it be hoisted (`$facet` / pre-query)? Get owner
  sign-off before Step 3 (design-review process).
- [ ] **Step 1: Write failing tests** — the Jira repro
  `Where(e => e.Title == c.Set<Employee>().OrderBy(e2 => e2.Title).FirstOrDefault()!.Title)` (expect ids 2,3) in
  `NativeOnly`; assert explicit `DriverLinq` still throws (driver can't express it → loud, unchanged); a test that the
  temp `__sq` field never appears in materialized entities, a `GroupBy` key, or a projection.
- [ ] **Step 2: Run** — FAIL (`ExpressionNotSupportedException` via silent fallback).
- [ ] **Step 3: Implement** per the design; register the lookup **at the commit point only** (no mutate-then-decline);
  keep `MarkNotNativelyRepresentable()` for every other shape.
- [ ] **Step 4: Run; Step 5: rewrite spec baselines** (`AssertTranslationFailed` → real assertions) and review MQL;
  **Step 6: mutation check; full suite EF8/9/10.** **Step 7: Commit** `EF-322: native uncorrelated scalar subquery (EF-220 shape A)`.

---

## Task 9: Tests-and-closure — EF-228, EF-252

**Files:**
- Test: `tests/…FunctionalTests/Query/NativeCardinalityTests.cs` (or the existing Average tests)
- Verify only: `tests/…SpecificationTests/…/NorthwindMiscellaneousQueryMongoTest.cs:4125` (`Throws_on_concurrent_query_first`)

- [ ] **Step 1 (EF-228):** add native functional tests for `Average` over `float`, `float?`, `decimal`, and grouped
  `g.Average(x => x.FloatMember)`: oracle = LINQ-to-objects (`Average` accumulates in double, casts to float → 1.6500001).
  Expect PASS; if the grouped float path throws `TruncationException` (agent's unconfirmed suspicion — it reads through
  `SingleSerializer`'s `RepresentationConverter`), fix by reading through a double serializer and narrowing.
  Explicit `DriverLinq` float `Average` stays a known throw — add a test pinning it; fixing it is the optional
  bridge rewrite (`Average(e => (double)sel)` + cast in `MongoClientWrapper.ExecuteScalar<T>` ~:153), **not** in scope (D5).
- [ ] **Step 2 (EF-252):** run `Throws_on_concurrent_query_first`, `_list`, `_last` under default and explicit
  `DriverLinq`; confirm no other `Throws_on_concurrent*` override marks a failure. Commit `bd3f2dfd` says it is fixed.
- [ ] **Step 3: Commit** tests `EF-322: cover native float/decimal Average; verify concurrent-query detector (EF-228, EF-252)`.

---

## Ordering and parallelism

1. **Silent-first, independent, parallelizable** (distinct files; give each subagent its own scratch subdir and its own
   build output — do not build the same tree concurrently): Task 2 (EF-255), Task 3 (EF-217), Task 4 (EF-459).
   Serialize their *commits* and rerun the affected suites after each merge — Tasks 2/3/4/5 all edit
   `MongoEFToLinqTranslatingExpressionVisitor.cs`.
2. Task 1 (EF-460/443) — independent; own file.
3. Task 5 (EF-337) — after 2–4, touches the same bridge and translator files and has the highest regression risk.
4. Tasks 6, 7, 8 — native breadth; strictly sequential (all change dispatch), each with a **full-suite** gate.
   Task 8 is blocked on the Step 0 design sign-off.
5. Task 9 — any time.

## Out of scope

- EF-220 sub-shape B (cross joins over unrelated roots) and the EF-X022 filtered/paged join inner.
- Host-`timezone` rendering for Local-kind date parts (EF-459 follow-up), `$rand` mapping, `$toInt` conversions.
- Driver changes (EF-247 DriverLinq leg, EF-255 driver `PartialEvaluator`) — file against the driver, not here.
- Anything not still reproducing on `EF-322c` (EF-215, 218, 227, 253, 357, 358, 359, 445, 456).

## Self-Review

- **Spec coverage:** every reproducing ticket maps to a task — 460/443→1, 255→2, 217→3, 459→4, 337→5, 247→6, 436→7,
  220 (A)→8, 228/252→9; 220 (B) explicitly deferred.
- **Placeholders:** implementation steps for Tasks 1, 3, 5–8 describe the change at the anchor the read-only analysis
  identified rather than showing final code; this is deliberate — those root causes are unconfirmed until Step 2 of each
  task runs, and prescribing code before the failing test would be guessing. Re-verify each cited line before editing.
- **Known uncertainty:** EF-443's root cause; EF-220/436 upstream test shapes (no decompiler available); whether the
  `Random` projection path is client-evaluated (Task 2 pins it first).
