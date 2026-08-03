# Decline a join whose inner sequence is paged (CSHARP-6017) — design spec

**Branch:** `NativeQueryOngoing` @ `365391f` · driver 3.10.0 · **JIRA: EF-366 (PLACEHOLDER — a ticket must be
filed before the first commit; the commit message and PR title must start with the real number).**

**Status:** design + plan for review. No `src/` change has been made.

Root cause is already established and is **not** re-derived here — see
`scratchpad/task1-groupby-rootcause.md`. One-paragraph recap: the EF-344 slice changed
`MongoQueryableMethodTranslatingExpressionVisitor.TranslateGroupBy` from upstream's `=> null` to "return a valid
shaped query and `MarkNotNativelyRepresentable()`", so unsupported `GroupBy` shapes now route to the
driver-LINQ fallback and **execute** instead of failing EF translation. Behind that doorway is driver bug
**CSHARP-6017**: driver 3.10 translates a join whose inner is an *uncorrelated* `Skip`/`Take`/subquery by
folding the inner's `$sort`/`$skip`/`$limit` into the **correlated** `$lookup` sub-pipeline, where they apply
per-outer-row over a ≤1-document key match instead of once over the whole inner sequence. Result: silently
wrong rows.

**The decision is already made and is not re-opened here:** hard-decline the CSHARP-6017 shape provider-side so
the query fails cleanly instead of returning wrong data. What this document settles is *where*, *how narrowly*,
and *what pins it*.

---

## 1. The calibration measurement

The predicate's precision is the whole risk: too broad and it declines queries that are **currently correct**,
which is worse than the bug. Everything below was measured on this branch at driver 3.10, not inferred.

### 1.1 Method

Two throwaway probe classes (`ZzzCalibrationProbeMongoTest`, `ZzzCalibrationProbe2MongoTest`, derived from
`NorthwindGroupByQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>`, deleted afterwards) ran 20
query shapes against the seeded Northwind data, dumping the emitted MQL and comparing each row count against
an **in-memory** LINQ-to-Objects computation over the fully materialized collections (so the expected value
involves no server-side translation). Raw logs:
`scratchpad/cal-native.log`, `cal-EF8.log`, `cal-EF9.log`, `cal2-native.log`.

Test-shape enumeration was done by reading the **actual query bodies** in the local EF Core source
(`/Users/arthur.vickers/code/efcore/test/EFCore.Specification.Tests/Query/*.cs`), not from test names, and
cross-referenced against the retained baseline sweeps `scratchpad/specsweep-365391f/{native,nativeonly}.trx`.

### 1.2 What is wrong today (measured)

| Probe | Shape | Expected | Actual | Verdict |
|---|---|---|---|---|
| PA | `Orders.Join(Customers.OrderBy(City).Skip(10).Take(50), o=>o.CustomerID, c=>c.CustomerID, …)` — **no GroupBy** | 453 | **0** | WRONG |
| PB | same, inner `OrderBy` only, no paging | 830 | 830 | correct |
| PC | inner is a reshaping `Select` subquery, no paging | 830 | 830 | correct |
| PD | inner is a reshaping `Select` subquery **+ `Take(20)`** | 181 | **830** | WRONG |
| PG | whole-entity join result, inner `OrderBy(_id).Take(5)` | 48 | **830** | WRONG |
| PT | non-FK join (`c.City equals e.City`), inner `OrderBy(City).Take(5)` | ≠27 | 27 | WRONG |
| PL | `…Join(inner.OrderBy.Skip.Take).GroupBy().Select()` (the diagnosis repro) | 49 | **0** | WRONG |
| PM | `Take` on the **outer** only, plain inner | 100 | 100 | correct |
| PE | **correlated** inner + `OrderBy`/`Take` via `SelectMany(c => Orders.Where(o => o.CustomerID == c.CustomerID)…Take(2))` | 177 | `InvalidOperationException` "could not be translated" | already declines |
| PF | **correlated** nav-collection inner + `Take`: `SelectMany(c => c.Orders.OrderBy(…).Take(2))` | 177 | `InvalidOperationException` "could not be translated" | already declines |
| PN | **filtered `Include`** `Include(c => c.Orders.OrderBy(_id).Take(2))` | 2 | 2 | correct, **native** |
| PO | filtered `Include` with `Skip(1).Take(2)` | 2 | 2 | correct, **native** |
| PP | plain collection `Include` | 6 | 6 | correct |
| PR | reference `Include` | 2 | 2 | correct |
| PQ | projected collection subquery + `Take` | — | `InvalidOperationException` | already declines |
| PS | `GroupJoin` + `DefaultIfEmpty`, inner `Take(4)` | — | `ExpressionNotSupportedException` | already fails |
| PI | `Join_GroupBy_Aggregate_in_subquery`'s inner **promoted to top level** | — | `NativeTranslationNotSupportedException` | existing guard fires |
| PJ | the same subquery **nested** as an outer join's inner | 133 | **0** | WRONG — flag lost across nesting |

Five findings that shape the design:

1. **The hole is not GroupBy-specific.** PA / PD / PG / PT return silently wrong data with **no `GroupBy`
   anywhere**. The GroupBy doorway is how the *spec suite* noticed; the user-facing hazard is the whole
   `Join`/`GroupJoin`/`LeftJoin` family. This is the strongest single argument for the chosen fix over the
   rejected "widen the GroupBy guard" and "skip the tests" options.
2. **The wrongness is exactly correlated with `Skip`/`Take` on the inner**, not with "is a subquery".
   PB and PC (fold of `$sort` alone, or no fold at all) are correct; PD (same subquery plus `Take`) is wrong.
   That is also precisely what separates the six CSHARP-6017-skipped `NorthwindJoinQueryMongoTest` cases from
   their **currently-green** non-`Take` siblings (`Join_customers_orders_with_subquery`,
   `Join_customers_orders_with_subquery_anonymous_property_method`). So the predicate is *paging on the inner*,
   full stop.
3. **The "uncorrelated" qualifier is real but needs no test.** `Queryable.Join`/`GroupJoin`/`LeftJoin` take
   their inner as an *argument*, so it is uncorrelated **by construction**; a correlated paged inner can only be
   written as `SelectMany`, which reaches `TranslateSelectMany` (`=> null`) and already fails EF translation
   outright — measured, PE and PF. A guard sited in `TranslateJoinCore` therefore gets the qualifier for free
   and cannot over-decline correlated shapes, because they never arrive there.
4. **Filtered `Include` is the one plausible collateral, and it is untouched.** PN/PO put a legitimate
   per-outer-row `$sort`/`$skip`/`$limit` inside a native `_lookup_Orders` sub-pipeline and are **correct**.
   They contain no `Queryable.Join` at all (the paging lives on a navigation, handled by the provider's own
   `LookupExpression` machinery), so `TranslateJoinCore` is never invoked. A guard sited in the driver-LINQ
   bridge instead would have to distinguish this case explicitly.
5. **The existing wrong-data flag does not survive nesting.** PI declines; PJ — the *same* subquery used as an
   outer join's inner — executes and returns 0 rows. `MarkGroupByFallbackUnsafe()` writes to the
   *intermediate* `MongoQueryExpression` and the gate only ever reads the outermost one. This is a
   pre-existing hole in the EF-344 guard, independent of CSHARP-6017, and it is what
   `Join_GroupBy_Aggregate_in_subquery` needs.

Identical results on **EF8, EF9 and EF10** for PA, PB, PC, PD, PG, PI, PJ, PL, PM (`cal-EF8.log`,
`cal-EF9.log`, `cal-native.log`). PH differs only because cross-collection reference predicates are EF10-only,
which is unrelated.

### 1.3 The critical output: what flips from "green and correct" to "declining"

Candidate predicates considered, each scored by the number of **currently-green, data-asserting** tests it
would turn into declines:

| # | Candidate predicate | Green-and-correct tests lost | Verdict |
|---|---|---|---|
| P1 | inner side of a `Join`/`GroupJoin`/`LeftJoin` carries `Skip` or `Take` | **0** | **CHOSEN** |
| P2 | P1, **plus** propagate an inner's existing wrong-data verdict to the outer | **0** | **CHOSEN** (second half) |
| P3 | inner is any *reshaping subquery* (projection/grouping/`Distinct`), with or without paging | ≥2 (`Join_customers_orders_with_subquery`, `Join_customers_orders_with_subquery_anonymous_property_method`; both measured correct — PC) | rejected: over-declines |
| P4 | either side (outer **or** inner) carries `Skip`/`Take` | ≥1 (`PM`-shaped outer paging is correct; `Join_complex_GroupBy_Aggregate`'s own outer `Take(100)` is emitted correctly at top level) plus every `Take`-then-`Join` user query | rejected: the outer's paging is emitted at pipeline top level and is correct |
| P5 | any `Skip`/`Take` anywhere in the join subtree, including navigations | ≥2 (PN, PO — filtered `Include`) | rejected: breaks correct filtered `Include` |
| P6 | widen `IsGroupByFallbackUnsafe` to all GroupBy+join orderings (diagnosis option C, no narrowing) | ≥3 (`Join_GroupBy_Aggregate`, `Self_join_GroupBy_Aggregate`, `Join_groupby_anonymous_orderby_anonymous_projection` — all measured correct join-then-group) | rejected, and already ruled out by the owner |

**Result for the chosen predicate (P1 + P2): zero currently-green-and-correct tests are lost.**

The full candidate set in the ported suites (only the Northwind bases are ported —
`ComplexNavigationsQueryTestBase.GroupJoin_on_right_side_being_a_subquery` and
`ComplexTypeQueryTestBase.Project_entity_with_complex_type_pushdown_and_then_left_join` also match the shape
but those suites are not derived from, so they do not run):

| Test | Current disposition on this branch @3.10 | Under P1/P2 |
|---|---|---|
| `NorthwindGroupBy…Join_complex_GroupBy_Aggregate` | **fails** — executes, 0 rows, expected 29 | declines → **passes as written** |
| `NorthwindGroupBy…GroupJoin_complex_GroupBy_Aggregate` | **fails** — executes, 27 rows, expected 20 | declines → **passes as written** |
| `NorthwindGroupBy…Join_GroupBy_Aggregate_in_subquery` | **fails** — executes, 0 rows, expected 133 | declines via P2 → **passes as written** |
| `NorthwindJoin…Join_customers_orders_with_subquery_with_take` | `[ConditionalTheory(Skip = "CSHARP-6017…")]` | declines → un-skip + retarget (§2.5) |
| `NorthwindJoin…Join_customers_orders_with_subquery_anonymous_property_method_with_take` | skipped, same | declines → un-skip + retarget |
| `NorthwindJoin…Join_customers_orders_with_subquery_predicate_with_take` | skipped, same | declines → un-skip + retarget |
| `NorthwindJoin…GroupJoin_simple_subquery` | skipped, same | declines → un-skip + retarget |
| `NorthwindJoin…GroupJoin_Subquery_with_Take_Then_SelectMany_Where` | skipped, same | declines → un-skip + retarget |
| `NorthwindJoin…GroupJoin_customers_employees_subquery_shadow_take` | skipped, same | declines → un-skip + retarget |
| `NorthwindJoin…Client_Join_select_many` | passes; the **EF** translation fails first (client method in the key selector), `AssertMql()` empty | unchanged — the QMTEV `Visit` override throws `CoreStrings.TranslationFailed` before the gate runs |
| `NorthwindJoin…Inner_join_with_tautology_predicate_converts_to_cross_join` | passes by asserting `ExpressionNotSupportedException` + MQL baseline `Customers.` | **exception type and MQL baseline change** → re-baseline (bookkeeping, not a data regression) |
| `NorthwindJoin…Left_join_with_tautology_predicate_doesnt_convert_to_cross_join` | same | same → re-baseline |
| `NorthwindSelect…Reverse_in_join_inner_with_skip` | passes by asserting `ExpressionNotSupportedException` (EF10) / `AssertTranslationFailed` (EF8/9) | same → re-baseline |
| `NorthwindMiscellaneous…Lifting_when_subquery_nested_order_by_simple` / `_anonymous` | pass via `AssertTranslationFailed` (EF-level failure, EF-216) | unchanged — EF translation fails before the gate |
| `NorthwindGroupBy…Join_GroupBy_Aggregate`, `Self_join_GroupBy_Aggregate`, `Join_groupby_anonymous_orderby_anonymous_projection` | pass with data (`await base.…`) | unchanged — join-then-group, plain inner, no paging |
| `Join_GroupBy_Aggregate_on_key`, `Join_GroupBy_Aggregate_multijoins`, `_single_join`, `_with_another_join`, `_distinct_single_join`, `_with_left_join`, `GroupJoin_GroupBy_Aggregate`(1–5) | already `AssertTranslationFailed` | unchanged |

So the **cost of the chosen predicate is: 0 green-and-correct tests, and 3 currently-red-but-pinned assertions
that need their exception type / MQL baseline updated** (`Inner_join_with_tautology_predicate_converts_to_cross_join`,
`Left_join_with_tautology_predicate_doesnt_convert_to_cross_join`, `Reverse_in_join_inner_with_skip`). The
narrow predicate **is** achievable; the fall-back-to-skip-and-ticket outcome the task allowed for is not needed.

### 1.3a Independent corroboration — exhaustive suite sweep

The table above came from a targeted regex over the EF Core bases plus per-test source reading. A **separate,
independent** exhaustive pass was then run over *every* provider spec class, mapping each to its EF base,
mechanically extracting the inner argument of every `join…in…on`, `.Join`/`.GroupJoin`/`.LeftJoin`/`.RightJoin`,
`.SelectMany(`, and every non-first `from x in …` clause, classifying by paging / reshaping / correlation, and
joining the result against `native.trx` + `nativeonly.trx` by test name. It also verified no drift by re-running
the extraction against `git show v10.0.8:` copies of the nine base files (identical hit set). It confirms the
headline number and adds four things worth recording:

1. **Buckets A, C and D are empty — independently.** There is **no** currently-green, *data-asserting* test in
   either suite whose join inner carries `Skip`/`Take` (A), or is a reshaping subquery without paging (C), or is
   correlated with `Skip`/`Take` (D). Total across the three: **0**. This is the same conclusion as §1.3,
   reached by a different method.
2. **The control group is explicit and must stay green.** Five tests are green *on data* with an inner that
   **does** fold into a `$lookup` sub-pipeline, with a `$sort`/`$match` but no paging — the B1 skips minus
   `Take`: `Join_customers_orders_with_subquery`, `Join_customers_orders_with_subquery_predicate`,
   `GroupJoin_customers_employees_subquery_shadow`, `GroupJoin_DefaultIfEmpty2`,
   `GroupJoin_SelectMany_subquery_with_filter`. Five more are green with paging applied **after** the join
   (`Join_Customers_Orders_Skip_Take` and siblings, `Join_take_count_works`), and two with paging on the
   **outer** (`GroupJoin_DefaultIfEmpty3`, `Reverse_in_join_outer_with_take`). These twelve are the
   regression net for P3/P4/P5 over-declining, and they are already in the suite — no new coverage needed.
3. **A larger to-verify set of expected-failure tests than §1.3 lists**, all currently green only in the sense
   of "it still throws": `GroupJoin_subquery_projection_outer_mixed`, `Projection_when_arithmetic_mixed`,
   `Projection_when_arithmetic_mixed_subqueries`, `Where_subquery_anon`, `Where_subquery_anon_nested`,
   `SelectMany_mixed`, `OrderBy_SelectMany`, `Tags_on_subquery`, `Select_Where_Subquery_Equality`, and — the
   group worth naming — **seven bulk-update tests** whose inner is `Orders.Where(< 10300).OrderBy(…).Skip(0)
   .Take(100)`: `Delete_with_join`, `Delete_with_LeftJoin`, `Delete_with_LeftJoin_via_flattened_GroupJoin`,
   `Delete_with_RightJoin`, `Delete_with_cross_join`, `Delete_with_cross_apply`, `Delete_with_outer_apply`.
   **Expected to be unaffected, for a structural reason:** all of these currently fail at the **EF** level —
   `TranslateExecuteDelete` / the unsupported operator returns `null`, the QMTEV `Visit` override sees
   `NotTranslatedExpression` and throws `CoreStrings.TranslationFailed` **inside the QMTEV**, i.e. strictly
   before `MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery` ever reads the flag. Setting a flag on a
   query whose translation then fails is inert. The same reasoning covers `Client_Join_select_many` and
   `Lifting_when_subquery_nested_order_by_*`. This is a *prediction*, and the validation task's by-name trx
   comparison against the retained baselines is exactly what confirms or refutes it — any of these flipping is a
   re-baseline, not a data regression, but it must be seen and accounted for rather than discovered later.
4. **A real blind spot, flagged not fixed.** All four Include suites define their own
   `AssertTranslationFailed` as a bare `try { await query(); } catch { return; }`
   (`NorthwindIncludeQueryMongoTest.cs:1317`, `NorthwindIncludeNoTrackingQueryMongoTest.cs:1201`,
   `NorthwindEFPropertyIncludeQueryMongoTest.cs:1323`, `NorthwindStringIncludeQueryMongoTest.cs:1324`), which
   swallows an xUnit data-assertion failure as well as a translation failure. Forty Include cases with paging on
   a cross-join / correlated-apply inner are green through it, so their green status is **not** evidence that
   those shapes fail to translate. Those forty are all `SelectMany`-shaped (cross join / apply), so they never
   reach `TranslateJoinCore` and this slice does not change them — but the masking helper means the Include
   suites cannot *validate* anything here either. Fixing it is out of scope (it would light up unrelated
   failures); recorded as a follow-up in §5. A separate pre-existing defect found in the same pass, also out of
   scope: **31 overrides call the wrong base method** (mostly `base.DTO_subquery_orderby`), so they assert
   nothing about their own query — 13 of them are `SelectMany`/`Select` shapes in this family.

### 1.4 Confidence and what would falsify it

*High* for everything marked "measured". The one item resting on source reading rather than instrumentation is
that `innerQueryExpression.Select.HasPaging` is *observable inside* `TranslateJoinCore` (instrumenting it would
require a `src/` edit, which this task forbids). The chain is:
`MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall` dispatches every allowed `Queryable` call to
`NativeSlotPopulator.PopulateNativeSlots(shapedQuery, …)`, whose `QueryableMethods.Skip`/`Take` arms call
`mongoQ.Select.AppendSkip/AppendLimit`; EF Core's own base visitor
(`src/EFCore/Query/QueryableMethodTranslatingExpressionVisitor.cs:386`) hands **that same**
`ShapedQueryExpression` instance straight to `TranslateJoin`. Both ends verified in source. The sibling flag
`IsGroupBy` is set and read across exactly the same seam and is measured working (PI). **The plan writes the
failing test first**, so a wrong assumption surfaces in Task 2 step 2 rather than at the end; §2.2 records the
contingency (walk `innerQueryExpression.CapturedExpression`).

---

## 2. The eight settled questions

### 2.1 Q1 — Where does the guard live, and by what mechanism?

**Decision: in `MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore`, as a sibling
wrong-data flag on `MongoSelectDefinition`, consumed by the existing
`MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition` → `NativeDisposition.HardDecline`.**
The alternative considered and rejected is a guard in the driver-LINQ bridge
(`MongoEFToLinqTranslatingExpressionVisitor`, near `LeftJoin`/`StripJoinForLookup`), as the diagnosis suggested.

Reasons, in order of weight:

1. **`TranslateJoinCore` is the single choke point and it is on the path for every measured wrong case.**
   `TranslateJoin`, `TranslateGroupJoin` and `TranslateLeftJoin` all delegate to it (`:1268`–`:1281`), and PA /
   PD / PG / PT / PL / PJ and all six skipped Join tests go through it (PS proves translation *completes* —
   the driver throws afterwards — so `TranslateJoinCore` ran).
2. **It gets the "uncorrelated" qualifier for free** (§1.2 finding 3). The bridge sees correlated and
   uncorrelated inners alike and would have to re-derive correlation — the exact over-decline risk the
   calibration was run to avoid.
3. **It does not see filtered `Include`** (§1.2 finding 4). A bridge-sited guard would see PN/PO's legitimate
   per-outer-row paging and would need a carve-out.
4. **The decline machinery already exists and satisfies the hard constraint for free.**
   `ClassifyNativeDisposition` → `HardDecline` → `VisitShapedQuery` (`:169`) throws
   `NativeTranslationNotSupportedException` at **compile time**, which is one of the three types
   `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync` accepts. It is already unit-pinned
   (`NativeDispositionTests`) and already honours `MongoQueryMode`. A bridge-sited guard would need a new
   throw site and a new mode plumb.
5. **Blast radius.** `MongoEFToLinqTranslatingExpressionVisitor` is *also* the bulk-operation bridge — EF9+
   `ExecuteUpdate`/`ExecuteDelete` go through it. Guarding there widens the change into the bulk path for no
   measured benefit.

The one advantage of the bridge — it sees the whole tree, so nesting is free — is answered by the explicit
inner→outer propagation (P2), which is one statement and *additionally* closes the pre-existing EF-344 nesting
hole measured as PI-vs-PJ. That makes the propagation a net win rather than a cost of this siting.

**Shape of the change** (three `src/` files, all `internal`):

```csharp
// MongoSelectDefinition.cs — alongside _isGroupByFallbackUnsafe

private bool _isPagedJoinInnerFallbackUnsafe;

/// <summary>
/// <see langword="true"/> when this query contains a Join/GroupJoin/LeftJoin whose INNER sequence carries
/// paging (Skip/Take). Driver 3.10 mistranslates that shape — CSHARP-6017 — by folding the uncorrelated
/// inner's $sort/$skip/$limit into the CORRELATED $lookup sub-pipeline, where they run per-outer-row over a
/// key-matched subset of at most one document instead of once over the whole inner sequence, so the
/// driver-LINQ fallback returns silently WRONG rows (measured: 0 rows where 453 are correct; 830 where 181
/// are correct). Like <see cref="IsGroupByFallbackUnsafe"/> this must hard-decline rather than fall back.
/// </summary>
internal bool IsPagedJoinInnerFallbackUnsafe => _isPagedJoinInnerFallbackUnsafe;

/// <summary>Records the CSHARP-6017 paged-join-inner shape. Also marks the query non-native.</summary>
internal void MarkPagedJoinInnerFallbackUnsafe()
{
    _isPagedJoinInnerFallbackUnsafe = true;
    _hasUnsupportedOperator = true;
}

/// <summary>
/// <see langword="true"/> when ANY wrong-data-on-fallback provenance has been recorded — a GroupBy+Join
/// (<see cref="IsGroupByFallbackUnsafe"/>) or a paged join inner
/// (<see cref="IsPagedJoinInnerFallbackUnsafe"/>). This is what the gate reads: both mean "the driver-LINQ
/// fallback executes and returns wrong rows", so both hard-decline identically. The two flags stay separate
/// so the decline message can name the actual cause and so the CSHARP-6017 one can be deleted wholesale
/// when the driver is fixed.
/// </summary>
internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe || _isPagedJoinInnerFallbackUnsafe;

/// <summary>
/// Copies any wrong-data provenance from <paramref name="inner"/> onto this select. A join whose inner is
/// itself a SUBQUERY containing the offending shape records the verdict on the INTERMEDIATE
/// MongoQueryExpression, and the gate only ever reads the OUTERMOST one — measured: the
/// Join_GroupBy_Aggregate_in_subquery inner declines correctly when promoted to top level but executes and
/// returns 0 rows when nested. Propagation is what makes the verdict nesting-insensitive.
/// </summary>
internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
{
    if (inner._isGroupByFallbackUnsafe) MarkGroupByFallbackUnsafe();
    if (inner._isPagedJoinInnerFallbackUnsafe) MarkPagedJoinInnerFallbackUnsafe();
}

// Paging predicate — HasPaging deliberately scans _pipelineOps only (its consumer gates a PRE-terminal
// GroupBy); the join guard must see paging wherever it was recorded, including a Take composed AFTER a set
// operation, which lands in _trailingOps.
internal bool HasPagingAnywhere
    => HasPaging || _trailingOps.Exists(o => o is MongoSkipOp or MongoLimitOp);
```

The gate changes by one line — `ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)` passes
`q.Select.IsFallbackWrongData` instead of `q.Select.IsGroupByFallbackUnsafe`. The **pure** overload's signature
is unchanged in arity; its parameter is renamed `isGroupByFallbackUnsafe` → `isFallbackWrongData` (so the
XML doc stops claiming "GroupBy+Join"), which requires updating the named arguments in
`NativeDispositionTests`. The `VisitShapedQuery` message becomes cause-specific (see the plan) so a user
reading it is told which shape was declined.

### 2.2 Q2 — The exact detection predicate

Inserted in `TranslateJoinCore` immediately after the existing `IsGroupBy` / `IsDistinct` block, as two
**independent** `if` statements (not extra arms of that `if`/`else if` chain — `MarkPagedJoinInnerFallbackUnsafe`
is strictly stronger than the `IsDistinct` arm's `MarkNotNativelyRepresentable`, so the chain must not be able
to swallow either):

```csharp
if (innerQueryExpression.Select.HasPagingAnywhere)
{
    outerQueryExpression.Select.MarkPagedJoinInnerFallbackUnsafe();
}

outerQueryExpression.Select.PropagateFallbackWrongDataFrom(innerQueryExpression.Select);
```

Stated precisely: **a `Join`/`GroupJoin`/`LeftJoin` hard-declines when the inner `MongoSelectDefinition` has
recorded a `MongoSkipOp` or `MongoLimitOp` in either op list, or has itself already recorded a wrong-data
verdict.** Nothing about the outer side is examined (PM is correct and must stay so). No correlation test
(§1.2 finding 3). No test for "is a reshaping subquery" (P3 was rejected; PC is correct).

**AS BUILT — the predicate is "paging was RECORDED anywhere", and "recorded" needed a third channel beyond the
two op lists. Amended in the final fix wave (EF-366 finding I1) after a measured silent-wrong-data hole.**
`NativeSlotPopulator.PopulateNativeSlots` returns BEFORE its `AppendSkip`/`AppendLimit` arms when
`HasTerminalOperator && !IsSetOpTerminalOnly`, so a `Skip`/`Take` composed after a NON-set-op native terminal is
swallowed and appears in *neither* op list. Measured on that shape —

```csharp
db.Orders.Join(
    db.Regions.Select(r => new { r.Country }).Distinct().Take(1),
    o => o.Country, r => r.Country, (o, r) => new { o.Country, o.Amount })
```

— `TryBindDistinctFromProjection` sets `IsDistinct`, the `Take(1)` was swallowed, `HasPagingAnywhere` was
`false`, `TranslateJoinCore` took the graceful `else if (IsDistinct)` arm instead of the hard decline, and the
query **executed and returned all five orders where at most two is correct** — silently, under **default
`Native`** as well as explicit `DriverLinq`. The captured MQL showed the inner's
`$project`/`$group`/`$replaceRoot`/`$limit: 1` folded bodily into the correlated `$lookup`'s own sub-pipeline,
i.e. exactly CSHARP-6017 reached through a second doorway.

The fix is a third recording channel, **not** the captured-tree scan the contingency below proposes:
`MongoSelectDefinition.MarkSawUnrecordedPaging()` sets a flag that `HasPagingAnywhere` ORs in, called from the
three `NativeSlotPopulator` sites that DECLINE a `Skip`/`Take` instead of lowering it (the post-terminal early
return, and the two `TranslateCountExpression`-returned-`null` arms). It is exact and cannot over-decline: it
says only "a `Skip`/`Take` was seen on this sequence and not lowered", which is precisely the condition under
which the paging survives into the captured chain the fallback executes and the driver folds it. A captured-tree
scan was explicitly rejected as the fix — it is the shape most likely to over-decline.

Of the three call sites, only the post-terminal early return has been shown reachable from ordinary LINQ; the two
count-translation arms are defence-in-depth (EF parameterizes a captured or computed count, so
`TranslateCountExpression` essentially always succeeds). See §2.9.

Whole-entity `Distinct().Take(n)` was and is safe by a different route: no `Projection` to bind, so `IsDistinct`
is never set, so the `Take` *is* recorded as a `MongoLimitOp` — verified as a control in the same probe. A
`GroupBy`-terminal inner is safe by accident, because the `GroupBy` arm hard-declines first.

**Can `TranslateJoinCore` see it?** Yes — see §1.4. Skip/Take never reach `TranslateSkip`/`TranslateTake` (both
`=> null` and both unreachable, because `VisitMethodCall`'s switch does not list them so they never go to
`base.VisitMethodCall`); they are recorded by `NativeSlotPopulator.PopulateNativeSlots` onto the inner's own
`MongoSelectDefinition`, and EF hands that same instance to `TranslateJoin`.
**Contingency, if the test written first in Task 2 shows the flag is not visible:** fall back to scanning the
inner's captured tree — `innerQueryExpression.CapturedExpression` is set on every visited shaped query
(`MongoQueryableMethodTranslatingExpressionVisitor.cs:162`) — for a `MethodCallExpression` whose generic method
definition is `QueryableMethods.Skip` or `QueryableMethods.Take`, descending `Arguments[0]`. The predicate's
*meaning* is unchanged either way; only its implementation moves.

**One known false-positive channel, deliberately accepted:** `AppendLimit` is also used for the synthesized
`First`/`Single` reducer limit. A reducer result is not an `IQueryable`, so it cannot be a `Queryable.Join`
inner, and no shape in the measured set hits this. If one is ever found, narrow to
"`MongoLimitOp`/`MongoSkipOp` and `Cardinality?.Reducer is null`".

### 2.3 Q3 — Behaviour under explicit `MongoQueryMode.DriverLinq`

**Decision: match `IsGroupByFallbackUnsafe` exactly — explicit `DriverLinq` still executes the query.**

This is free: `ClassifyNativeDisposition` returns `HardDecline` only when `mode != MongoQueryMode.DriverLinq`,
and the new flag rides the same `IsFallbackWrongData` input. Reasons to keep it that way rather than decline
unconditionally:

- `DriverLinq` is documented as the user's opt-in to "the previous path, warts and all" — it is the escape
  hatch that makes "native became the default" non-breaking (see the `AGENTS.md` versioning rubric). A mode
  that starts *refusing* queries the old path ran would undercut that.
- One decline concept, one mode rule. Two flags with different mode semantics would be a trap for the next
  reader, and `Query/AGENTS.md` already documents the `DriverLinq`-executes-anyway rule once.
- It is what makes the expiry tripwire in §2.6 possible: `DriverLinq` is the only mode that still reaches the
  driver, so it is where the CSHARP-6017 defect stays observable.

The wrong-data caveat is already attached to that opt-in in the gate's message and in `Query/AGENTS.md`; the
message change in §2.1 keeps it.

### 2.4 Q4 — Multi-version (EF8 / EF9 / EF10)

**Decision: no `#if`. One code path for all three.**

Evidence: `TranslateGroupBy` (the doorway) has no version guard, and `TranslateJoinCore` is reached on all
three — `TranslateJoin` and `TranslateGroupJoin` are unconditional; only `TranslateLeftJoin`/`TranslateRightJoin`
sit behind `#if !EF8 && !EF9`, and on EF8/EF9 a left join is expressed as `GroupJoin` + `DefaultIfEmpty` +
`SelectMany`, which still routes through `TranslateGroupJoin` → `TranslateJoinCore`. Measured: PA, PB, PC, PD,
PG, PI, PJ, PL, PM produce **identical** results and identical MQL on EF8, EF9 and EF10
(`cal-EF8.log`, `cal-EF9.log`, `cal-native.log`) — the hazard is present and the fix applies on all three.

Test-side, `#if` may still be needed in the *re-baselined* spec overrides where a baseline already differs by
version (`Left_join_with_tautology_predicate_doesnt_convert_to_cross_join` and
`Reverse_in_join_inner_with_skip` already carry `#if EF8 || EF9`); those keep whatever shape the measured
per-version behaviour requires.

### 2.5 Q5 — The six upstream-skipped `NorthwindJoinQueryMongoTest` tests

**Decision: this slice un-skips all six and retargets them to a translation-failure assertion.** Leaving them
would mean the suite carries `Skip = "CSHARP-6017: … returning wrong results"` for a hazard the provider now
refuses to execute — a false claim, and exactly the kind of stale-doc drift this project keeps paying for. It
also conflicts with the project's "never XUnit `Skip` in spec suites" convention, which the chosen fix was
partly chosen to honour.

Mechanics: the decline throws `NativeTranslationNotSupportedException`, which derives from `Exception`, **not**
`InvalidOperationException` — so EF Core's base `AssertTranslationFailed` (which requires an
`InvalidOperationException` carrying `CoreStrings.TranslationFailed`) will not accept it. Retarget each of the
six to `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(() => base.…(async))` **at the call site**,
rather than adding a `protected new static AssertTranslationFailed` shadow to
`NorthwindJoinQueryMongoTest` — a shadow would silently loosen the ~10 existing `AssertTranslationFailed`
call sites in that file from "EF said translation failed, with that message" to "something threw".

**Scope implication, flagged not hidden:** this is one extra task touching one extra test file
(`NorthwindJoinQueryMongoTest.cs`, 6 methods) plus 3 re-baselines in two more files
(`NorthwindJoinQueryMongoTest.cs`, `NorthwindSelectQueryMongoTest.cs`). It does **not** expand the `src/`
change. Two of the six —`GroupJoin_Subquery_with_Take_Then_SelectMany_Where` and
`GroupJoin_customers_employees_subquery_shadow_take` — go through EF navigation-expansion rewrites that could
in principle land on `SelectMany` rather than `Queryable.GroupJoin`; PT measured the latter's shape reaching the
join path, but the plan's task states the contingency explicitly: any of the six that still executes and
returns wrong data keeps a `Skip`, with its reason rewritten to say the provider guard does not reach it, and
is recorded as a follow-up. No test is left asserting something untrue.

### 2.6 Q6 — Expiry plan

This guard exists **only** because of a driver defect and must be removable. **Decision: a self-announcing
tripwire test, plus a `TODO(CSHARP-6017)` naming the exact removal steps.** A driver-version check is rejected:
we cannot know which driver release fixes CSHARP-6017 (3.10.1? 3.11?), and a version comparison would be a
second guess layered on the first.

The tripwire is a `[Fact]` in `NativeJoinPagedInnerDeclineTests` that runs the offending shape under explicit
`MongoQueryMode.DriverLinq` — the only mode that still reaches the driver — and asserts the **wrong** row
count that CSHARP-6017 produces, with a comment saying so. When the driver stops folding, that assertion
fails, which is the announcement.

**Removal criteria, stated explicitly.** Delete the guard when *all* of the following hold:

1. CSHARP-6017 is resolved in the driver version pinned by `Versions.props`.
2. `Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017` fails (the tripwire has
   tripped) — this is the operative signal, not the ticket status.

Then, in one commit:

1. Delete `MongoSelectDefinition.MarkPagedJoinInnerFallbackUnsafe` / `IsPagedJoinInnerFallbackUnsafe` /
   `HasPagingAnywhere` / `MarkSawUnrecordedPaging`, and the three `MarkSawUnrecordedPaging` call sites in
   `NativeSlotPopulator` (see §2.2 "AS BUILT").
2. Delete the `HasPagingAnywhere` block in `TranslateJoinCore` — **keeping `PropagateFallbackWrongDataFrom`**,
   which fixes an unrelated EF-344 nesting hole and must **not** be removed with it.
3. Collapse `IsFallbackWrongData` back to `IsGroupByFallbackUnsafe`. That is a **rename**, not a deletion, in
   `tests/…/UnitTests/Query/NativeTranslation/NativeDispositionTests.cs`: rename the `Classify` helper's
   `isFallbackWrongData` parameter back to `isGroupByFallbackUnsafe` and rename the four tests that pass it. Their
   BEHAVIOUR is permanent — the `GroupBy`+`Join` half of the union survives the driver fix.
4. **Delete only the guard-only tests in `NativeJoinPagedInnerDeclineTests`, NOT the whole file.** The file
   carries survivors. DELETE: `Join_with_paged_inner_declines_under_native`,
   `Join_with_paged_inner_declines_under_native_only`,
   `Join_with_paged_inner_never_returns_the_wrong_rows_under_native`,
   `Join_with_paged_inner_still_runs_under_driver_linq`, `GroupJoin_with_paged_inner_declines_under_native`,
   `Join_with_inner_paged_after_a_set_operation_declines_under_native`,
   `Join_with_a_projected_Distinct_then_paged_inner_declines_under_native`, and the tripwire
   `Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017`. KEEP:
   `Join_with_paged_outer_still_runs_and_is_correct` and
   `Join_with_reshaped_unpaged_inner_still_runs_and_is_correct` — general join-correctness controls and this
   branch's own over-decline nets, which must still pass once the guard is gone (re-running them is how the
   removal is proved safe); and `Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native`, which
   pins the PERMANENT `PropagateFallbackWrongDataFrom` and has no paging at all. KEEP but amend:
   `Join_with_grouped_outer_and_paged_inner_reports_both_causes` (its message assertion degenerates to the single
   `GroupBy`+`Join` fragment). If the whole file is deleted, the two controls go with it and general join coverage
   is silently lost.
5. **Same DELETE/KEEP split for the six new `MongoSelectDefinitionTests` cases.** DELETE:
   `HasPagingAnywhere_is_false_with_no_paging`, `HasPagingAnywhere_sees_pipeline_ops`,
   `HasPagingAnywhere_sees_trailing_ops_after_a_set_op`, `HasPagingAnywhere_sees_declined_unrecorded_paging`,
   `MarkPagedJoinInnerFallbackUnsafe_sets_the_flag_and_forces_fallback_route`. KEEP:
   `Fallback_wrong_data_is_false_by_default`, `PropagateFallbackWrongDataFrom_copies_both_provenances_independently`
   (narrowing it to the `GroupBy` provenance only), and `PropagateFallbackWrongDataFrom_a_clean_inner_is_a_no_op`
   — all three pin the permanent EF-344 mechanism.
6. Revert the six `NorthwindJoinQueryMongoTest` retargets to `await base.…` with real MQL baselines, revert
   `Join_complex_GroupBy_Aggregate` / `GroupJoin_complex_GroupBy_Aggregate` to `await base.…`, and re-baseline the
   3 exception-type assertions (including `Reverse_in_join_inner_with_skip`).
7. Revert *only* the paged-inner sentences of the `Query/AGENTS.md` note (keep the group-first `GroupBy`+`Join`
   paragraph and the `PropagateFallbackWrongDataFrom` sentence).
8. **Decide `BREAKING-CHANGES.md` by release order, and it is decidable at removal time by looking at
   `gh release list`: if 8.5.0 / 9.2.0 / 10.1.0 (whichever line is in flight) has ALREADY SHIPPED with the
   guard, the entry is permanent history and must STAY — a released version did behave that way, and readers
   whose code started throwing need to find out why; if the driver fix lands FIRST, so the guard never appears in
   any released package, DELETE the entry, because it would document a change that never shipped.** (Per the
   `AGENTS.md` rubric: something added and removed within one unreleased development cycle is not a break.) The
   two tautology re-baselines in §2.5 are unaffected either way.

Every one of those sites carries a `TODO(CSHARP-6017)` marker, so
`grep -rn "CSHARP-6017" src tests docs BREAKING-CHANGES.md` is the removal checklist. **`BREAKING-CHANGES.md`
must be in the grep root** — it is outside `src tests docs`, and step 8 above is the reason it matters. (The plan
document's own final-verification step already greps with `BREAKING-CHANGES.md` appended; this spec is the
authoritative version and now matches it.)

**Do NOT touch `Join_GroupBy_Aggregate_in_subquery`.** Unlike the other two `…GroupBy_Aggregate` spec cases
above, its inner has no paging at all — it declines because a wrong-data verdict on its inner subquery is
propagated to the outer query via `PropagateFallbackWrongDataFrom` (§2.1's independent, permanent mechanism),
not via the CSHARP-6017 paged-inner guard. Its `AssertTranslationFailed` baseline (see the comment at
`NorthwindGroupByQueryMongoTest.Join_GroupBy_Aggregate_in_subquery`, which already says "this is NOT the
CSHARP-6017 guard and it stays after the driver is fixed") is permanent and must **not** be reverted when
CSHARP-6017 is fixed — reverting it would leave the suite red (the query would go back to executing and
returning 0 rows instead of 133) and is exactly the mistake this note exists to prevent.

### 2.7 Q7 — `BREAKING-CHANGES.md`

Settled with evidence, in two parts, because the answer differs by which configuration of the released
assembly you measure against.

**The baseline.** `gh release list` → latest overall `v10.0.2` (2026-06-03); latest per EF line `v10.0.2`,
`v9.1.2`, `v8.4.2`. All three pin `<CSharpDriverVersion>3.9.0</CSharpDriverVersion>`
(`git show v10.0.2:Versions.props`), and the provider's only driver reference is
`<PackageReference Include="MongoDB.Driver" Version="$(CSharpDriverVersion)" />` — a NuGet **minimum**, so the
shipped package's dependency is `MongoDB.Driver (>= 3.9.0)` and **driver 3.10.0 is a permitted, supported
configuration of the released package**.

1. **Against the released package at its resolved driver (3.9.0): not a break.** Measured in the diagnosis
   (`scratchpad/probe-prerebase-39.log`): at 3.9 every one of these queries throws
   `ExpressionNotSupportedException` during driver translation. After the fix they throw
   `NativeTranslationNotSupportedException`. Before = throw, after = throw; only the exception **type** on an
   **unsupported** operation differs, which the `AGENTS.md` rubric carves out explicitly as *not* a break.
2. **Against the released package with the driver independently upgraded to 3.10.0: a real behaviour change.**
   Before = the query executes and returns **silently wrong rows** (measured: PA returns 0 where 453 is
   correct; PD returns 830 where 181 is correct — and PA has no `GroupBy`, so this is reachable from the
   *released* provider, not only from this branch). After = a clean throw. That is observable to a real user in
   a configuration the shipped package permits, and the next release will pin driver 3.10 for everyone.

**Decision: yes, add an entry** to the current unreleased section of `BREAKING-CHANGES.md`
("Breaking changes in 8.5.0 / 9.2.0 / 10.1.0"), scoped and honest: title it as a behaviour change — a join whose
inner sequence is paged now throws instead of returning wrong results — state that on driver 3.9 it already
threw, that on driver 3.10 (which the next release pins, and which the released package permits) it silently
returned wrong rows, name CSHARP-6017, and give the mitigation (materialize the paged inner first, e.g.
`var page = inner.OrderBy(…).Skip(n).Take(m).ToList();` then join against `page`, or `UseQueryMode(
MongoQueryMode.DriverLinq)` to keep the old — wrong — execution). Rationale: the rubric's carve-out covers
exception-*type* changes, not silent-success → exception, and the whole point of a breaking-changes log is that
someone whose code stopped running can find out why in one place. Cheap to write, expensive to omit.

**Second half of the question: no entry for the wrong-data exposure the branch itself introduced.** The EF-344
GroupBy doorway has never shipped — it exists only on `NativeQueryOngoing`. Per the rubric ("A public API that
was added and then changed or removed within the current unreleased development cycle never shipped, so it is
not a break") an unreleased intermediate state is not a break and documenting it would mislead readers into
thinking a released version behaved that way. It belongs in the PR description, not in `BREAKING-CHANGES.md`.

### 2.8 Q8 — What test pins each behaviour

Every guard here is mutation-pinned: for each row, deleting the named production code makes the named test
fail. New file `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinPagedInnerDeclineTests.cs`
(modelled on `NativeGroupByTests`'s `GroupByJoinDbContext` two-collection Orders/Regions fixture, which already
exists for exactly this purpose).

| Behaviour | Test | Deleting what makes it fail |
|---|---|---|
| Paged inner declines under `Native` | `Join_with_paged_inner_declines_under_native` | the `HasPagingAnywhere` block |
| …and under `NativeOnly` | `Join_with_paged_inner_declines_under_native_only` | **nothing — GUARD-INDEPENDENT, corrected in place.** This row used to say "same", i.e. that deleting the `HasPagingAnywhere` block makes it fail. It does not: `QueryableMethods.Join` is absent from `NativeSlotPopulator.IsNativeRepresentableSlotOperator`, and `PopulateNativeSlots` still runs for `Join` after the switch, so the catch-all sets `Route = Fallback` for **every** join query and `NativeOnly` throws with or without the guard. The test documents the `NativeOnly` disposition (it must be the same clean decline, not a different failure), and its own in-file comment now says so, so a future reader does not mistake it for a pin |
| **Wrong DATA does not come back** (own `[Fact]`) | `Join_with_paged_inner_never_returns_the_wrong_rows_under_native` | the `HasPagingAnywhere` block — the query then executes and returns 5 rows where 3 are correct |
| `GroupJoin` / left-join form declines too | `GroupJoin_with_paged_inner_declines_under_native` | same |
| Paging on the **outer** only still runs, correctly | `Join_with_paged_outer_still_runs_and_is_correct` | over-broad predicate (P4) |
| Inner subquery **without** paging still runs, correctly | `Join_with_reshaped_unpaged_inner_still_runs_and_is_correct` | over-broad predicate (P3) |
| Filtered `Include` with `Skip`/`Take` still runs natively and correctly | `Filtered_include_with_paging_still_runs_and_is_correct` | over-broad predicate (P5) |
| Explicit `DriverLinq` still executes | `Join_with_paged_inner_still_runs_under_driver_linq` | making the decline mode-independent |
| Nested wrong-data verdict propagates | `Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native` | `PropagateFallbackWrongDataFrom` |
| **Expiry tripwire** | `Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017` | nothing in the provider — it fails when the *driver* is fixed |
| Gate classification (unit) | `NativeDispositionTests` cases for `isFallbackWrongData: true` | the `mode != DriverLinq && isFallbackWrongData` branch |
| `HasPagingAnywhere` (unit) | `MongoSelectDefinitionTests.HasPagingAnywhere_*` | the `_trailingOps` half of `HasPagingAnywhere` (`…_sees_trailing_ops_after_a_set_op`) / the `_sawUnrecordedPaging` half (`…_sees_declined_unrecorded_paging`) |
| Paging swallowed by a NON-set-op terminal still declines (§2.2 "AS BUILT") | `Join_with_a_projected_Distinct_then_paged_inner_declines_under_native` | the `MarkSawUnrecordedPaging` call in `NativeSlotPopulator`'s post-terminal early return — the query then executes and returns 5 rows where at most 2 are correct |
| Spec-level (paging-guard-pinned — reverts when the driver is fixed) | `Join_complex_GroupBy_Aggregate`, `GroupJoin_complex_GroupBy_Aggregate`, the 6 retargeted Join tests, `Reverse_in_join_inner_with_skip` | the guard (`HasPagingAnywhere` / `MarkPagedJoinInnerFallbackUnsafe`) — most go back to executing and returning wrong data, which `AssertNativeTranslationFailedAsync` rejects; `Reverse_in_join_inner_with_skip` instead falls back to the driver's own pre-existing CSHARP-5836 Reverse-in-a-join rejection, so its assertion doesn't actually distinguish "guard present" from "guard absent" (see task-9-report.md §5/§7) — it is listed here for the guard's *sake*, not as an independent mutation pin |
| Spec-level (propagation-pinned — **PERMANENT**, does NOT revert with the driver fix) | `Join_GroupBy_Aggregate_in_subquery` | `PropagateFallbackWrongDataFrom` — NOT the CSHARP-6017 guard; this case has no paging at all, and declines because a wrong-data verdict on its inner subquery is propagated to the outer query (the independent, permanent EF-344 fix). Reverting it alongside the paging-guard row above would leave the suite red — see §2.6's explicit "Do NOT touch" note |

The wrong-data assertion is in **its own `[Fact]`**, and it is written so the data comparison is the reachable
branch under mutation — a recorded lesson is that a wrong-rows assertion placed *after* a decline assertion in
the same method is unreachable exactly when the guard is deleted:

```csharp
string[]? rows = null;
var ex = Record.Exception(() => rows = Query());
if (ex is null)
{
    Assert.Equal(["FR:EU", "UK:EU", "UK:EU"], rows);   // reached iff the guard was deleted -> fails on 5 rows
}
else
{
    Assert.IsType<NativeTranslationNotSupportedException>(ex);
}
```

Seed arithmetic for the `GroupByJoinDbContext` fixture (Orders: US/100, US/200, UK/50, UK/25, FR/300;
Regions: US/NA, UK/EU, FR/EU): `Orders.Join(Regions.OrderBy(r => r.Country).Take(2), o => o.Country,
r => r.Country, …)` — correct answer is the 3 rows whose country is FR or UK; the CSHARP-6017 fold keeps every
order's single match and returns **5**. A 3-vs-5 gap that is not an empty-vs-nonempty gap is deliberate: an
"is it empty" assertion would also pass for an unrelated failure.

### 2.9 Known limitations of the recording predicate (final fix wave, EF-366)

Recorded because the guard's subject is really the captured method chain the driver-LINQ fallback executes, while
its *implementation* is a set of flags on `MongoSelectDefinition`. Those two agree only where a code path records
what it saw. §2.2 "AS BUILT" closes the one channel where they measurably disagreed; this section enumerates what
is left.

1. **Reachable, closed.** A `Skip`/`Take` composed after a NON-set-op native terminal (a projected `Distinct`) was
   swallowed by `NativeSlotPopulator`'s post-terminal early return, so `HasPagingAnywhere` was false and the join
   fell back with the paging still in the chain — **measured returning five rows where at most two is correct,
   silently, under default `Native`.** Closed by `MarkSawUnrecordedPaging` at that early return; pinned by
   `NativeJoinPagedInnerDeclineTests.Join_with_a_projected_Distinct_then_paged_inner_declines_under_native` and
   `MongoSelectDefinitionTests.HasPagingAnywhere_sees_declined_unrecorded_paging`.
2. **Not shown reachable, closed anyway (defence-in-depth).** A `Skip`/`Take` whose count expression is neither a
   `ConstantExpression` nor a query parameter makes `NativeSlotPopulator.TranslateCountExpression` return `null`,
   which declines the operator without recording it — the same class of hole. `MarkSawUnrecordedPaging` is called
   there too, but no ordinary-LINQ spelling has been found that reaches it (EF parameterizes a captured or
   computed count, so the translation essentially always succeeds), so those two call sites are unpinned by any
   test. Do not delete them as dead code; do not claim them as measured either.
3. **Open, and out of scope for this slice — §5 follow-up 7, restated here because this is where a reader will
   look for it.** The guard closes the hazard *at the join*, not at each doorway that can put an
   otherwise-declined operator into the captured chain. Every other EF-level gate this branch relaxed
   (`Distinct`, `Union`/`Concat`, `Intersect`/`Except`, `SelectMany`, `OfType`) is a potential second doorway onto
   the same driver defect via a route that is not a `Skip`/`Take` at all. Item 1 above is a worked example of that
   audit finding something real, which raises rather than lowers the value of performing it.

---

## 3. Non-goals

- **Native join translation is NOT in scope.** Making `Join`/`GroupJoin`/`LeftJoin` go native is the strategic
  answer (it removes the dependency on the driver's LINQ provider for these shapes entirely) and is the next
  planned slice per the recorded cutover order. This slice deliberately does not start it; the guard is written
  to be deleted, not extended.
- **Fixing the driver is NOT in scope.** CSHARP-6017 is a driver defect. This slice does not attempt a
  provider-side rewrite of the folded pipeline, and does not attempt to emit a correct pipeline for the shape.
- **Widening the existing GroupBy guard beyond nesting propagation is NOT in scope.** Diagnosis option C
  (order/nesting-insensitive `IsGroupByFallbackUnsafe`) costs ≥3 green-and-correct tests (§1.3, P6) and was
  ruled out by the owner. Only the *nesting* half (P2) is taken, because it is measured to cost nothing and is
  needed by `Join_GroupBy_Aggregate_in_subquery`.
- **Re-litigating the choice of hard-decline over skip-and-ticket is NOT in scope.** Settled by the owner; the
  calibration in §1 confirms the narrow predicate is achievable, so the fall-back option is moot.
- **The `Complex_query_with_group_by_in_subquery5` exception-type drift is handled but is not this guard.**
  See §4.
- **No `[Skip]` attribute is added anywhere.** That is a stated reason the hard-decline option was chosen.

---

## 4. The other three failing GroupBy methods

Not caused by CSHARP-6017; test-expectation drift that must be cleared in the same slice so the suite is honest.

| Method | Measured behaviour at 3.10 | Treatment |
|---|---|---|
| `GroupBy_with_group_key_access_thru_nested_navigation` | driver-LINQ fallback now **works and returns correct data**; the override still asserts a failure, so it fails with "no exception thrown" | retarget to `await base.…` plus the real MQL baseline. It is a query that got *better*; asserting a failure is now false. |
| `GroupBy_with_group_key_being_nested_navigation` | still fails, but now *after* a real pipeline executed (`FormatException` deserializing a whole-entity `$group` key), so the recorded `"""OrderDetails."""` translation-failure fingerprint drifted to real `$project`/`$lookup` text | re-baseline the MQL only; keep `AssertTranslationFailed`. |
| `Complex_query_with_group_by_in_subquery5` | driver 3.10 changed the failure **type**: `ConstantExpressionToAggregationExpressionTranslator` now BSON-serializes a `MongoQuery<Customer,Customer>` constant and dies in `BsonClassMap.Freeze()` with duplicate element name `Expression` (was `ExpressionNotSupportedException` at 3.9) | **Decision: accept the type here, no separate ticket for the test.** Add `typeof(BsonSerializationException)` to this one call site's accepted list (not to the file-wide shadow, which would loosen 240+ other assertions). Justification: per the `AGENTS.md` rubric an exception-type change on an unsupported operation is explicitly not a break, the query is unsupported either way, and the shape is unrelated to this slice's guard — bundling it into a separate ticket would leave a red test on the branch for no gain. The *driver-side* ugliness (a translation error surfacing as a serialization error) is recorded as a follow-up in §5 instead. |

---

## 5. Recommended follow-ups for the owner to action (no ticket filed by this work)

1. **Comment on CSHARP-6017** that the fold also reaches `GroupBy` shapes and — newly measured here — plain
   non-`GroupBy` joins, with the driver-only repro: `outer.Join(inner.OrderBy(x => x.Key).Skip(10).Take(50), …)`
   emits `$skip`/`$limit` **inside** the `$lookup` sub-pipeline. Include the measured counts (0 vs 453 correct;
   830 vs 181 correct; 27 vs correct for a non-FK key) and note that the non-`Take` sibling is correct, so the
   defect is specifically order/cardinality-sensitive folded operators.
2. **New CSHARP candidate (i):** `ConstantExpressionToAggregationExpressionTranslator` attempts to BSON-serialize
   a `MongoQuery<,>` constant (an un-inlined subquery `IQueryable` left in the tree) and fails in
   `BsonClassMap.Freeze()` with "duplicate element name 'Expression'". Should be a translation error, not a
   serialization error.
3. **New CSHARP candidate (ii):** a whole-entity `$group` key is deserialized with `BsonClassMapSerializer<T>`
   instead of the registered serializer, giving `FormatException: Element '_id' does not match any field or
   property of class Customer`.
4. **File the EF ticket for this slice** (the `EF-366` placeholder above) before the first commit.
5. **The four Include suites' masking `AssertTranslationFailed`** (`try { await query(); } catch { return; }`)
   swallows wrong-data assertion failures as well as translation failures, hiding 40 paged-inner Include cases
   (§1.3a item 4). It should be replaced with `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync`, which
   deliberately rejects xUnit assertion exceptions. Out of scope here because it will light up unrelated
   failures across four suites.
6. **31 spec overrides call the wrong base method** (mostly `base.DTO_subquery_orderby`), so they are green while
   asserting nothing about their own query — 13 are `SelectMany`/`Select` shapes in this family. Pre-existing;
   its own ticket.
7. **Bounded audit, deferred:** every other operator whose EF-level gate this branch relaxed (`Distinct`,
   `Union`/`Concat`, `Intersect`/`Except`, `SelectMany`, `OfType`) is a potential second doorway onto the same
   driver defect. The guard in this slice closes the hazard at the join, not at each doorway, so this is now a
   completeness check rather than a correctness risk — but it has not been performed.

---

## 6. Documentation that must change as part of this work

`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md:39` currently ends with:

> **Join-then-group** (an aggregate over a join result) falls back **correctly** and is unaffected — do not
> conflate the two orderings.

Driver 3.10 falsified that premise: join-then-group falls back correctly **only when the join's inner sequence
is not paged** (measured — `Join_complex_GroupBy_Aggregate` is join-then-group and returns 0 rows instead of
29). That line must be corrected and the new decline documented next to the existing one, or the next reader
will re-derive this whole investigation.
