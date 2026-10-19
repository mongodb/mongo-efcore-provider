# Native GroupBy step 2: post-group `Where` over projected aliases, and `$push` list projection

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the last two `NorthwindGroupByQueryMongoTest` methods that pass only via driver-LINQ,
`GroupBy_count_filter` and `GroupBy_selecting_grouping_key_list`, translate natively. Along the way, fix the
latent wrong-field hazard in how post-group operators resolve members.

**Architecture:**
- **Task 1.** After a keyed `GroupBy(...).Select(...)`, a `Where` over the flattened output is currently
  rejected by NativeSlotPopulator's post-terminal guard. It will instead resolve members against the Select's own
  output aliases, taken from `MongoSelectDefinition.Projection`, and record into `PostGroupOps`. `ActiveOps`
  already routes there, and the lowerer emits `PostGroupOps` after the flatten `$project`. The same alias-keyed
  resolution replaces the current `DistinctAliasScope = select.Grouping` reuse on the post-GroupBy terminal
  aggregate path. That reuse resolves by *pre-flatten key-part name*, which can differ from the Select alias.
- **Task 2.** A `g.Select(e => e.X).ToList()` / `ToArray()` projection member binds as a new `$push` accumulator
  whose output is read back as the array itself.
- **Task 3.** Spec suite, docs, and full three-version verification.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit (plain
`Assert.*`), Docker/TestContainers.

**Spec:** No separate spec doc. The design comes from the 2026-09-28 investigation (Background below) and the
parent design `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (the SP2 correction
note about `GroupBy_count_filter`, and SP8). This plan follows
`docs/superpowers/plans/2026-09-28-native-groupby-over-join-scope.md` (step 1, squashed as `20e2c647`).

## Background

The EF10 NativeOnly measurement at `20e2c647` leaves 4 failures (2 methods × async) in
`NorthwindGroupByQueryMongoTest`.

**`GroupBy_count_filter`:**
`Set<Order>().Select(e => new { e.OrderID, Name = "Order" }).GroupBy(o => o.Name).Select(g => new { Name = g.Key, Count = g.Count() }).Where(o => o.Count > 0)`.
Nav-expansion folds the first `Select` into the key, so the key arrives as the constant `"Order"`, which is
already supported. The decline is the post-terminal guard in `NativeSlotPopulator` (~:131). It exempts only a
slot op after a *projected Distinct* (`isPostDistinctSlot`). A throwaway spike that exempted `Where` and set
`translator.DistinctAliasScope = select.Grouping` passed the test's data assertions natively. It emitted
`$group` → `$project {Name: "$_id", Count: "$Count"}` → `$match {$expr: {$gt: ["$Count", 0]}}`.

**The hazard that makes the spike unsafe.**
`MongoExpressionTranslator.TryResolveMember` (`MongoExpressionTranslator.Members.cs` ~:107-122) with a
`DistinctAliasScope` matches a member name against the grouping's **key-part names** and returns that part's
`IProperty`. For a projected Distinct those names *are* the output aliases. For a keyed GroupBy the output
aliases are the **Select's** member names, which can differ. For example:
`GroupBy(o => new { o.CustomerID }).Select(g => new { CustomerID = g.Count(), C = g.Key.CustomerID }).Where(x => x.CustomerID > 5)`.
Here `x.CustomerID` would resolve to the string key property, and the constant would be serialized as a string,
while the output field `CustomerID` holds the count. The existing post-GroupBy terminal-aggregate path
(`NativeCardinalityBinder.TryBindAggregate` ~:270-274, `isPostGroupBySelectAggregate`) already sets
`DistinctAliasScope = select.Grouping` for keyed GroupBy. So the same hazard probably exists today for
`.All(x => x.CustomerID > 5)`-shaped queries. Task 1 must **probe that first** and fix it with the same mechanism.

**What the flattened output looks like.** `NativeGroupByBinder.TryBindGroupProjection` adds one
`MongoProjection(alias, read)` per Select member to `select.Projection`:
- `read` is `MongoElementRefExpression("_id" | "_id.<Part>", type)` for a key member;
- `MongoElementRefExpression(<accumulator OutputField == alias>, type)` for an accumulator;
- another node (computed/conditional, `MongoDocumentConstructionExpression`) for computed members.

The flatten `$project` writes each alias as a top-level field, so after it every alias is addressable as
`$<alias>`. The key is read back through a generic CLR serializer, because `HasDefaultKeySerialization` gates key
binding. So an alias-typed constant can be serialized by its CLR type without an `IProperty`.

**`GroupBy_selecting_grouping_key_list`:**
`Set<Order>().GroupBy(o => o.CustomerID).Select(g => new { g.Key, Data = g.Select(e => e.CustomerID).ToList() })`.
It declines because no accumulator arm matches in `TryBindGroupProjection` (the caller marks it at QMTEV ~:324).
EF produces `Enumerable.ToList(g.AsQueryable().Select(e => e.CustomerID))`. A spike added one arm in
`TryBindAccumulator` (`NativeGroupByBinder.cs` ~:577): `ToList`/`ToArray` over `Select(g, sel)` with a
`TryTranslateValue` operand gives `MongoGroupAccumulator(outputField, "$push", operand)`, with a flatten read
`MongoElementRefExpression(outputField, typeof(List<T>)/T[])`. That spike passed data assertions without shaper
changes: `BsonSerializerFactory.CreateTypeSerializer` builds `List<T>`/`T[]` serializers.

The four `GroupBy_select_grouping_list/_array/_composed_list/_composed_list_2` tests are **out of scope**. They
push whole entities, which need per-element entity materialization and tracking. Task 2 must explicitly
**decline entity-typed elements** so its arm can't be taken for them.

**Deliberate decisions (do not relitigate):**
- Task 1 covers **`Where`** after a finalized keyed GroupBy.Select, plus the terminal-aggregate path's alias
  resolution. `OrderBy`/`ThenBy`/`Skip`/`Take`/`Distinct` after the grouped Select stay declined: YAGNI, and a
  separate follow-up. So the post-terminal guard exemption is `Where`-only.
- Alias resolution is **by the Select's output alias, never the key-part name**. An alias that isn't in
  `select.Projection` declines. It never falls through to entity resolution.
- `Where` after a grouped Select over a **join scope** is in scope. Step 1's `ActiveOps` precedence already puts
  post-group conjuncts after `$group`. Step 1's test
  `NativeGroupByOverJoinTests.Post_group_where_after_a_confirmed_join_group_declines_cleanly` is expected to start
  going native. Its own comment says to flip it to `NativeAndParity` then.
- **`$push` element semantics:**
  - An element that is **null or missing** must appear as `null` in the list, because C# `g.Select(e => e.X)`
    yields null for a null `X`. Render the operand null-safely (`$ifNull: [operand, null]`, or the existing
    null-safe field form) whenever the element CLR type is nullable or a reference type. Measure what bare
    `$push` does with a missing field first, and record it.
  - A non-nullable element that may read an unmatched left-outer side **declines** (step 1's
    `MayReadAnUnmatchedJoinSide` rule).
  - Value-converted or non-default-represented element properties **decline**. The list is read back through a
    generic CLR serializer.
  - Element order inside the list follows `$group` input order, which is unspecified without a prior sort. This
    matches driver-LINQ; the spec test compares unordered. Accepted, and noted in a code comment.
- Pure-null-parameter query-dialect `{$lte: null}`, per-element `x.Rank == null` over missing, and the null
  parameter vs nullable-cast key NRE are out of scope (pre-existing, owner-deferred).

## Global Constraints

- Work directly on branch `native-EF-322-Native-LINQ-rebased` (no worktree). Commit messages start with
  `EF-322: `. **Do NOT push** — the owner decides.
- Preserve file BOMs. `src/` is nullable-enabled — annotate new types.
- Must compile and pass under `Debug EF8`, `Debug EF9`, `Debug EF10`; `#if` only if a version genuinely differs.
- xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset. Never enable test parallelization.
- A recognizer must not mutate then decline — stage into locals, commit only after every gate passes.
- Every new native shape: NativeOnly result == hand-computed oracle; also == DriverLinq where DriverLinq is correct
  (`NativeModeAssert.NativeAndParity`); where DriverLinq is wrong or throws, assert NativeOnly vs the hand oracle
  only and say why in a comment.
- Guards must be proven by mutation (disable → named test fails), results recorded.
- Stage only files you changed; never `nuget.config`.
- Scratch logs: `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/step2-task<N>/`.

## Review Focus

1. **An output alias that shares a name with a key part or an entity property but holds a different value**
   (e.g. `CustomerID = g.Count()`) must resolve to the alias's own field and type. Pinned by Task 1's collision
   tests, for both `Where` and the terminal-aggregate path.
2. **A post-group `Where` over a key alias that is null for some group** (nullable key, or a left-join unmatched
   side) must follow C# lifted semantics. Step 1's renderer null guard should cover `<`/`<=`. Pinned by a Task 1
   nullable-key test.
3. **A `Where` referencing a computed or constant key alias** (`Name = g.Key` where the key is the constant
   `"Order"`): correct native result or clean decline, never a wrong field. Pinned by the
   `GroupBy_count_filter`-shaped test.
4. **`$push` over a nullable element with null and missing values** yields `null` entries in the list. Pinned by
   Task 2's ragged test.
5. **`$push` over entity-typed elements** (`g.ToList()`, `g.Select(e => e).ToList()`) declines cleanly and never
   produces driver-class-map materialization. Pinned by Task 2's decline test.

---

### Task 1: Alias-keyed resolution after a keyed GroupBy.Select; native post-group `Where`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` and/or
  `MongoExpressionTranslator.Members.cs` (new alias-scope resolution)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (post-terminal guard
  ~:120-137; `Where` arm ~:156-170)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` (~:270-274)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` (root-collection cases)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByOverJoinTests.cs` (flip
  `Post_group_where_after_a_confirmed_join_group_declines_cleanly`)

**Interfaces:**
- Produces: one alias-scope mechanism on `MongoExpressionTranslator`. Suggested shape: an
  `internal IReadOnlyList<MongoProjection>? ProjectedAliasScope { get; set; }`, whose resolution maps a
  single-hop `x.<Alias>` on the lambda parameter to `MongoElementRefExpression(<Alias>, <projection read type>)`.
  The implementer may choose a different shape after reading the translator. It must be one mechanism, used by
  both NativeSlotPopulator's post-group `Where` and NativeCardinalityBinder's post-GroupBy aggregate path, and it
  must **never** consult key-part names or the entity for a keyed GroupBy.

- [ ] **Step 1: Probe the latent hazard (do this before any code change)**

Write a throwaway probe test (don't commit it, or delete it) in `NativeGroupByTests`, run it under NativeOnly and
DriverLinq, and record the results in the report:
```csharp
db.Entities.GroupBy(o => new { o.Country })
    .Select(g => new { Country = g.Count(), C = g.Key.Country })
    .All(x => x.Country > 1)
```
Use that file's `Order` model: key on a string property, alias shadowing it with a count. Also run an `Any` and
a `Count(pred)` variant. Hand-compute the answer from the seed. If native returns a wrong answer or throws a
`FormatException`/serialization error, you have confirmed the latent bug. Say so explicitly. If it declines, say
that instead.

- [ ] **Step 2: Write the failing tests** (functional, `NativeGroupByTests`; follow its existing context/seed
  helpers; if a nullable-key property is missing from its model, add a minimal one)

Each test asserts NativeOnly against a hand-computed expectation. Use `NativeAndParity` where DriverLinq is
correct.
1. `GroupBy_count_filter` shape on the file's model: a `Select` with a constant member, then `GroupBy` on it,
   `Select(g => new { Name = g.Key, Count = g.Count() })`, `.Where(o => o.Count > 0)`.
2. Accumulator alias `Where`: `GroupBy(o => o.Country).Select(g => new { g.Key, Total = g.Sum(o => o.Amount) }).Where(x => x.Total > <mid>)`.
3. Key alias `Where` under a **renamed** alias: `Select(g => new { Land = g.Key, N = g.Count() }).Where(x => x.Land == "UK")`.
4. **Collision (Review Focus #1):** `GroupBy(o => new { o.Country }).Select(g => new { Country = g.Count(), C = g.Key.Country }).Where(x => x.Country > 1)`.
   The result must be the groups whose **count** exceeds 1.
5. **Collision on the terminal-aggregate path:** the Step 1 probe query (`All`), plus an `Any(pred)` or
   `Count(pred)` variant, asserted against the hand oracle.
6. **Nullable key alias with `<` (Review Focus #2):** group on a nullable property (null for one group), then
   `Where(x => x.Key < <c>)`. The null group is excluded.
7. A `Where` with a compound predicate over two aliases (`x.N > 1 && x.Land != "US"`).
8. **Negative:** `Where(x => x.<AliasNotInProjection>)` can't compile in C#, so instead a `Where` whose body
   references something the alias scope can't resolve (e.g. a method call on an accumulator alias that has no
   translation). It declines cleanly (`DeclinesCleanly`).
9. **Negative:** `OrderBy(x => x.N)` after the grouped Select still declines (out of scope). Pin it with
   `DeclinesCleanly`, with a comment saying why.

Run them and confirm the new native ones fail with `NativeTranslationNotSupportedException`. Also confirm that
test 4/5 fails *or produces the wrong answer*: that is the evidence of the hazard.

- [ ] **Step 3: Implement**

1. **Alias scope.** Add the alias-scope resolution to the translator. For a member access on the lambda's
   parameter (single hop), look up the alias in the scope. If found, resolve to a top-level reference to that
   alias, typed from the projection's read expression. If not found, decline; never fall through to
   `DistinctAliasScope` or entity resolution. Make comparisons against a constant/parameter work for this node,
   the same way the translator already handles `MongoElementRefExpression` operands elsewhere. Read
   `TryResolveDistinctAliasComputedField` and the "Bare accumulator alias" arm (~:1110-1130) to see how element
   refs are compared and rendered today. **One mechanism**: don't add a third ad-hoc path. If the existing
   `DistinctAliasScope` computed-field handling can be generalized to cover this without changing projected-
   Distinct behavior, that's acceptable too. Whichever you pick, say which in the report.
2. **NativeSlotPopulator.** In the post-terminal guard, exempt `Where` when
   `select.IsGroupBy && !select.IsDistinct && select.Grouping != null && select.Cardinality == null`, i.e. a
   finalized keyed grouping with no terminal. Put the exemption beside `isPostDistinctSlot` and keep the same
   comment style. In the `Where` arm, set the alias scope from `select.Projection` for that state. If
   translation fails, the existing flow must mark the query non-native. Check that it does, and that nothing is
   recorded before the decline.
3. **Nested GroupBy.** Check that a later `GroupBy` over the filtered output (`...Where(...).GroupBy(...)`) is
   still handled correctly: `SnapshotPriorGroupingForNestedGroupBy` must carry or lower the `PostGroupOps` in the
   right place. If it can't, decline that combination and add a test.
4. **NativeCardinalityBinder.** For `isPostGroupBySelectAggregate` (keyed GroupBy, not Distinct), switch from
   `DistinctAliasScope = select.Grouping` to the alias scope. Keep the projected-Distinct path
   (`isPostDistinctAggregate`) on `DistinctAliasScope`; there key-part names are the aliases. Keep
   `isPostGroupBySelectlessMinMax` behavior unchanged. Check that the "Bare accumulator alias" arm (a SelfParam
   over a sole accumulator, e.g. `.Select(g => g.Sum(...)).All(v => v >= 0)`) still works. That shape has no
   named alias; it uses the reserved bare alias.
5. **Join scope.** Flip `NativeGroupByOverJoinTests.Post_group_where_after_a_confirmed_join_group_declines_cleanly`
   to `NativeAndParity` with the same hand expectation (`[("North", 3)]`), and rename it to say it goes native. If
   DriverLinq disagrees, assert against the oracle only and explain. Also check that the step-1 `Any(pred)` /
   `Count(pred)` post-group `DeclinesCleanly` tests in that file: if they now go native, flip them the same way,
   asserting the hand oracle.

- [ ] **Step 4: Run the tests** — all new tests pass; `NativeGroupByTests`, `NativeGroupByOverJoinTests`,
  `NativeDistinctTests` pass on EF10.

- [ ] **Step 5: Mutation proof** — (a) make the alias scope fall back to key-part-name resolution (the old
  behavior) → tests 4 and 5 fail; (b) remove the `Where` exemption → tests 1–3, 6, 7 fail with
  `NativeTranslationNotSupportedException`. Record.

- [ ] **Step 6: Regression** — build EF8/EF9/EF10 (0 errors, no new warnings); EF10 unit + full functional; the
  `NorthwindGroupByQueryMongoTest`, `NorthwindAggregateOperatorsQueryMongoTest`, `NorthwindMiscellaneousQueryMongoTest`
  spec classes in default mode (expect `GroupBy_count_filter` baseline drift and possibly others — record the
  failing list with first message lines; **don't fix spec tests in this task**; any failure that isn't a baseline
  mismatch or a "no exception thrown" on an `AssertTranslationFailed` override must be reported).

- [ ] **Step 7: Commit**
```bash
git commit -m "EF-322: native post-group Where over a keyed GroupBy's projected aliases; alias-keyed post-group resolution"
```

---

### Task 2: `$push` list projection member (`g.Select(e => e.X).ToList()` / `ToArray()`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` (`TryBindAccumulator`
  ~:577, or a new `TryBindPushAccumulator` called from it)
- Possibly modify: the `$group` accumulator renderer (`MongoPipelineFactory` / `MongoSelectLowerer`), if
  `"$push"` isn't passed through generically — check how `$addToSet` is emitted
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`; join cases in
  `NativeGroupByOverJoinTests.cs`

- [ ] **Step 1: Measure `$push` over null/missing** — with a tiny probe against the test server (driver-level
  aggregate or a throwaway EF probe), record what `{$group: {_id: ..., L: {$push: "$F"}}}` yields for a document
  where `F` is null and where `F` is missing. Record in the report; it decides whether the operand needs `$ifNull`.

- [ ] **Step 2: Write the failing tests** (hand oracle; `NativeAndParity` where DriverLinq agrees)
1. `GroupBy(o => o.Country).Select(g => new { g.Key, Data = g.Select(e => e.Country).ToList() })` — the spec
   shape; compare lists **unordered** (sort each list client-side before comparing).
2. `ToArray()` form, over a different scalar (e.g. an `int`/`decimal`).
3. **Ragged nullable element (Review Focus #4):** a nullable property null on one row and **missing** on another
   (insert that document via the raw collection without the field); the list for that group contains two `null`
   entries plus the values.
4. A computed element selector: `g.Select(e => e.Amount * 2).ToList()`.
5. **Decline (Review Focus #5):** `g.ToList()` and `g.Select(e => e).ToList()` (entity elements) — `DeclinesCleanly`
   (or NativeOnly throws + Native equals DriverLinq if DriverLinq succeeds; if DriverLinq throws there too, assert
   NativeOnly throws and say so).
6. **Decline:** element over a value-converted property (add one to the model if absent, e.g.
   `HasConversion<string>()` on an enum/int) — NativeOnly throws.
7. **Join scope:** in `NativeGroupByOverJoinTests`, `from w in Owners join o in Orders on w.Id equals o.OwnerId
   into gj from o in gj.DefaultIfEmpty() group o by w.Name into g select new { g.Key, Totals = g.Select(x =>
   (decimal?)x.Total).ToList() }` — Dora's list is `[null]`; and the non-nullable form
   `g.Select(x => x.Total).ToList()` declines (unmatched left-outer side).
8. The bare form `Select(g => g.Select(e => e.Country).ToList())` (no wrapping anonymous type), if it goes native;
   otherwise pin its decline.

- [ ] **Step 3: Implement** — the arm: `Enumerable.ToList`/`ToArray` (check EF's exact method forms in the
  expression, e.g. `Enumerable.ToList(Queryable.Select(g.AsQueryable(), sel))`) over a `Select` whose source is
  the grouping (`IsGroupingSource`); translate the selector body via the `MongoGroupElementTranslator`
  (`TryTranslateValue`); decline when the element type is an entity type or the translated operand isn't a scalar
  value; decline value-converted/non-default-serialized properties (reuse `HasDefaultKeySerialization` or the
  default-serialization requirement `TryTranslateValue` already applies — verify which covers it); decline a
  non-nullable element that `MayReadAnUnmatchedJoinSide`; wrap nullable/reference operands null-safely if Step 1
  showed missing is skipped; produce `MongoGroupAccumulator(outputField, "$push", operand)` and flatten read
  `MongoElementRefExpression(outputField, <List<T> or T[] CLR type>)`. Code comment on element order.

- [ ] **Step 4: Run the tests; Step 5: Mutation proof** — (a) drop the null-safe wrap → test 3 fails (if the wrap
  was needed); (b) drop the entity-element decline → test 5 changes behavior (fails); (c) drop the unmatched-side
  decline → test 7's non-nullable case fails. Record.

- [ ] **Step 6: Regression** — as Task 1 Step 6 (record the spec failure list; don't fix spec tests).

- [ ] **Step 7: Commit**
```bash
git commit -m "EF-322: native \$push list projection for GroupBy element selectors"
```

---

### Task 3: Spec suite, docs, full three-version verification

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/*MongoTest.cs` (as the runs require)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (Durable invariants)
- Modify: `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP2 correction note /
  SP8 — mark delivered)

- [ ] **Step 1: Classify** every spec failure on EF10 (default + NativeOnly) by message into (a) baseline mismatch,
  (b) "no exception thrown" on `AssertTranslationFailed`, (c) other → stop and report. By name, from TRX.
- [ ] **Step 2: Flip (b)** overrides per the upstream base body (`/Users/arthur.vickers/code/efcore/test/EFCore.Specification.Tests/Query/`),
  mode-split only where measured; **Step 3: regenerate (a)** baselines with a scoped `EF_TEST_REWRITE_BASELINES=1`
  run after confirming data assertions passed; `git diff` every rewrite.
- [ ] **Step 4: Docs** — AGENTS.md: one terse bullet: "Post-group operators after a keyed GroupBy.Select resolve by
  the Select's output alias (`<mechanism name>`), never key-part name or entity property; only `Where` is native
  (ordering/paging after the grouped Select decline)." plus one clause on `$push` (null/missing → null entries;
  entity elements decline). Design doc: mark SP2's `GroupBy_count_filter` note and SP8 delivered, pointing at this
  plan.
- [ ] **Step 5: Full verification** — build all three; full unit + functional + spec suites on EF8, EF9, EF10 (0
  failures); EF10 NativeOnly **full spec** failure set by name vs `20e2c647` (detached worktree under the scratch
  dir, removed afterwards): PASS→FAIL empty; list FAIL→PASS by name (expected: the two target methods × async,
  plus any other post-group-`Where` / `$push` shapes — each named and explained). Under EF10 NativeOnly the
  `NorthwindGroupByQueryMongoTest` class must have **0 failures**.
- [ ] **Step 6: Commit** — `EF-322: re-baseline specs for native post-group Where and $push; document invariants`.
