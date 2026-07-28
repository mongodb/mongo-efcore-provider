# Native non-canonical `Skip`/`Take` via an ordered stage list

**Date:** 2026-07-20
**Ticket:** EF-347 (SP6-remaining relational operators)
**Branch:** `EF-347-noncanonical-paging` (stacked off `4e30ad2`, the current native tip on `NativeQueryOngoing`)

## Context

The native query translator currently supports filter / sort / paging over whole-entity results, but only in a **canonical arrangement**: `MongoSelectDefinition` holds single `Predicate` / `Orderings` / `Offset` / `Limit` slots, and `MongoSelectLowerer.AppendCanonicalStages` emits exactly one fixed order — `$match → $sort → $skip → $limit`. `NativeSlotPopulator` accepts only the canonical shape and **rejects everything else to driver-LINQ fallback**:

- `Where` / `OrderBy` / `ThenBy` applied **after** paging (`PagingAlreadyApplied` guard).
- `Take` **before** `Skip`, or paging operators applied **more than once** (single-slot guards).

These are the "non-canonical" shapes. There are three families, all currently falling back:

- **(a) operator after paging** — `.Skip(1).Where(p)`, `.Skip(1).OrderByDescending(k)`, `.Take(2).OrderBy(k)`
- **(b) Take before Skip** — `.Take(10).Skip(5)`
- **(c) repeated paging** — `.Skip(a).Skip(b)`, `.Skip(a).Take(b).Skip(c).Take(d)`

## The finding: this is coverage, not correctness

The driver-LINQ fallback **already returns correct results** for all three families under `Native` mode — it only throws under `NativeOnly` (see `QueryModeGateTests.Native_where_after_skip_returns_correct_rows_via_fallback`, `Native_order_after_skip_returns_correct_rows_via_fallback`, `Native_order_after_take_returns_correct_rows_via_fallback`, and their `NativeOnly_*_throws` siblings). Users therefore see **no bug** today — these queries work, just not on the native pipeline. This slice moves them onto the native path.

Two consequences of that framing:

1. **The driver-LINQ fallback is a ready-made correctness oracle.** The bar is "native results == existing fallback results", which already exist as test expectations.
2. **The design doc's "pushdown-into-subquery — the hard part" framing does not apply.** That is a relational assumption. MongoDB's aggregation pipeline is **inherently sequential** and matches LINQ's left-to-right composition exactly: `.Take(10).Skip(5)` is just `$limit:10, $skip:5`; `.Skip(1).Where(p)` is just `$skip:1, $match:p`. **No sub-pipeline is needed.** The only real blocker is that the IR uses single slots + a fixed lowering order instead of an ordered stage list.

## Approach: full ordered-stage model

Replace the four single slots on `MongoSelectDefinition` with **one ordered list** of filter/sort/page ops. This is the clean long-term IR (in the spirit of the EF-330 / EF-332 refactors) — chosen over an incremental "canonical prefix + overflow tail" hybrid or a narrow pure-paging-only subset.

### 1. Core IR — `MongoSelectDefinition`

An ordered `List` of ops, each one of:

- `Match(MongoExpression predicate)`
- `Sort(IReadOnlyList<MongoOrdering> orderings)`
- `Skip(MongoExpression count)`
- `Limit(MongoExpression count)`

This ordered list *is* the source of truth for the pre-terminal pipeline. Convenience/back-compat accessors (`Predicate`, `Offset`, `Limit`, `HasPaging`, `Orderings`) are retained as needed by existing consumers, computed from or projected onto the list where the semantics are unambiguous (or those consumers are updated — see blast radius).

**Merge rules — chosen so a currently-native canonical query keeps emitting today's exact MQL:**

- **Where** → extend the last op if it is a `Match` (AND-combine, preserving `.Where(a).Where(b)` → a single `$match{a ∧ b}`); otherwise append a new `Match`.
- **OrderBy** → if the last op is a `Sort`, **replace** it (reproduces today's `ResetOrderings`, so `.OrderBy(k1).OrderBy(k2)` → `$sort{k2}`, unchanged); otherwise append a new `Sort`.
- **ThenBy** → extend the last `Sort` (LINQ typing guarantees an `OrderBy`/`ThenBy` immediately precedes it).
- **Skip** → append a `Skip`. **Take** → append a `Limit`.

A single primitive — **"AND a conjunct into the tail `Match`"** (extend if the last op is a `Match`, else append a new one) — implements `Where` and is reused by `OfType`'s discriminator conjunct and the cardinality predicate-injection (§3).

### 2. `NativeSlotPopulator`

Delete the rejection guards: `PagingAlreadyApplied` (on `Where` / `OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending`) and the single-slot `Skip` / `Take` checks. Each operator applies its merge rule from §1 instead. The **terminal guards are unchanged** (`HasTerminalOperator` / `IsPostGroupSlotOperator`): an operator after a `GroupBy` / `Distinct` / set-op / `SelectMany` still falls back exactly as today. Net: the populator gets simpler.

### 3. `NativeCardinalityBinder` — cardinality-after-paging relaxation

Remove the `if (injectsPredicate && select.HasPaging) return false;` guard. The injected predicate (the negated body for `All`; the plain body for a defensive `Count(pred)` / `Any(pred)`) is ANDed into the **tail** `Match` via the shared primitive from §1, so it correctly lands **after** any `$skip` / `$limit`. This makes `Take(n).All(pred)`, `Take(n).Count(pred)`, and `Take(n).Any(pred)` native and correct (`Take(n)` sees only the first `n` rows, then the predicate filters them). The reducer path's `select.Limit != null` check becomes "a `Limit` op is already present" — a reducer (`First`/`Single`) still declines to reconcile with a user `Take`, unchanged.

The **post-terminal guard (`HasTerminalOperator`) at the top of both `TryBindReducer` and `TryBindAggregate` is unchanged** — a cardinality operator after a `GroupBy` / `Distinct` still falls back.

### 4. Lowerer — `AppendCanonicalStages` only

Replace the four fixed `if`-blocks with a single walk that emits the ordered list in order. Everything downstream is **untouched**: `AppendLookupStages`, the set-operation `$unionWith` terminal, the `UnwindSource` `$unwind`/`$project`, the `$group` terminal, the `$project` terminal, and the scalar-aggregate terminal all still follow the filter/sort/page block. The set-operation **operand** path keeps working unchanged: `IsPlainWholeEntitySelect` still admits a filter/sort/paging-only operand, now expressed as the ordered list, and `AppendCanonicalStages(setOp.OperandSelect, …)` walks it.

### 5. Verification bar and the one consequence

Emission is now **faithful to arrival order**. A currently-native query whose operators were *not* written in canonical order (e.g. `.OrderBy(k).Where(p)` → today `$match,$sort`; now `$sort,$match`) emits **different MQL** — but **identical results**. This is provably result-safe: today's model only ever accepted as native the cases where reordering is result-equivalent, because it **rejected** anything after paging (the only non-commuting boundary). So faithful emission changes results for **no** currently-native query; it only changes the *order* of commuting stages, plus adds the newly-native shapes.

- **Results bar:** every currently-passing shape stays green with identical data; newly-native shapes match the driver-LINQ oracle. Full 3-version `/test-all` + a `MONGODB_EF_NATIVE_ONLY` spec sweep whose native pass-set can only **grow** (never shrink).
- **Expected cost:** some `AssertMql` spec baselines churn where a currently-native query's operators reorder. Measured empirically and re-baselined data-gated. Per the versioning rubric (`AGENTS.md`), changed emitted MQL is explicitly **not** a breaking change.

**Decision: faithful emission, not a canonical-normalizer.** A normalizer that reconstructs canonical order for the still-canonical cases would re-introduce exactly the reordering logic this refactor removes, and its "is this reorder result-safe?" analysis (a `Match` commutes past a `Sort` but not past a `Skip`/`Limit`) is itself error-prone. Faithful emission is simpler and correct by construction. The cost is visible baseline churn in the PR.

### 6. Scope boundaries (YAGNI)

- **In:** `Where` / `OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending` / `Skip` / `Take` in **any order and repetition**, over whole-entity results; plus predicate-injecting aggregates after paging (`Take(n).All(pred)` / `Count(pred)` / `Any(pred)`).
- **Out (stay fallback exactly as today):** a projected `Select` or a `GroupBy` applied **after** non-canonical paging; `$lookup`/Include interactions with post-paging operators. These keep their existing guards and their existing driver-LINQ fallback.

## Blast radius

- `Query/Expressions/MongoSelectDefinition.cs` — the ordered-list IR + merge helpers + retained accessors (core change).
- `Query/NativeTranslation/NativeSlotPopulator.cs` — remove rejection guards; apply merge rules.
- `Query/NativeTranslation/NativeCardinalityBinder.cs` — remove the paging guard; route the injected predicate to the tail `Match`.
- `Query/NativeTranslation/MongoSelectLowerer.cs` — rewrite `AppendCanonicalStages` to walk the ordered list.
- `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateOfType`'s `AddPredicateConjunct` call now goes through the tail-`Match` primitive (behaviour-preserving pre-terminal).
- Consumers reading `Predicate` / `Offset` / `Limit` / `Orderings` / `HasPaging` (the gate, `IsPlainWholeEntitySelect`, binders) — audited and either read through retained accessors or updated.
- Tests: new unit coverage in `NativeTranslation/` (populator + lowerer ordered-list emission), new `QueryModeGate*` / native functional tests asserting the three families go native (`NativeOnly` succeeds) and match the oracle, and re-baselined `AssertMql` specs where reordering churns.

## Out of scope / follow-ups

- A projected `Select` or `GroupBy` after non-canonical paging going native.
- Any normalization pass to minimize MQL churn (explicitly rejected above).
- The SP7 streaming materializer capstone (separate sub-project).
