# Spike B — spec-test delta for a single-level reference-`Include` native slice

> **Provenance.** Measured 2026-07-31; committed 2026-08-03 when the EF-368 slice branch was created.
> Companion document: `2026-08-03-native-reference-include-spike-findings-architecture.md` (spike A).
> **§B1's baseline is stale by design:** the 12 `NorthwindGroupByQueryMongoTest` `Native`-mode failures it
> reports were diagnosed and fixed as EF-366, so tip is no longer in that state. §B2 and §B3 — the
> reference-`Include` scope and re-baselining figures, which are what this document is kept for — are
> unaffected. The `…/scratchpad/…` paths in the retained-artifacts table are dead (session-scoped); the TRX
> files were rescued into the local gitignored `.superpowers/` tree and were never committed.

*Measured 2026-07-31 against `NativeQueryOngoing` tip `365391f` (post-rebase onto `upstream/main` `58e05a0`,
C# driver 3.10.0), EF10 specification suite. Read-only spike: no `src/` edits, no commits, no branch moves.*

**Retained artifacts** (durable, re-checkable):

| File | What |
|---|---|
| `…/scratchpad/specsweep-365391f/native.trx` | `Native` (default) sweep, run 1 |
| `…/scratchpad/specsweep-365391f/native-confirm.trx` | `Native` sweep, run 2 (solo, no concurrent sibling run) |
| `…/scratchpad/specsweep-365391f/nativeonly.trx` | `MONGODB_EF_NATIVE_ONLY=1` sweep |
| `…/scratchpad/specsweep-365391f/upstream-groupby.trx` | `NorthwindGroupByQueryMongoTest` only, at `upstream/main` `58e05a0` |
| `…/scratchpad/specsweep-365391f/*.log` | console logs for each |
| `…/scratchpad/scripts/parsetrx.py` | trx → per-test outcome/message/stack |
| `…/scratchpad/scripts/bucket.py` | failure-message bucketing, both orderings, rules in the docstring |
| `…/scratchpad/scripts/classify_include.py` | Include shape classification from EF Core test-base source |
| `…/scratchpad/scripts/mql_baselines.py` | `AssertMql` baseline presence per override |

(`…` = `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/323eba4f-8228-4bc2-ae7f-40ed6677ddfe`)

---

## B1. Post-rebase baseline — and a rebase regression

### Direct answer

**`Native` mode is NOT clean post-rebase. There are 12 failures, all in `NorthwindGroupByQueryMongoTest`.**
They are deterministic (reproduced twice, byte-identical failure sets), they are **not** present on
`upstream/main`, and they are therefore a **regression introduced on this branch by the rebase** — the
interaction of the native stack with the C# driver 3.9→3.10 bump. `58e05a0` re-baselined five
`Northwind*QueryMongoTest` files but **not** `NorthwindGroupByQueryMongoTest`, because on `upstream/main`
that class needed no change; on this branch it does.

Everything else reconciles **exactly** with the pre-rebase figures. The `NativeOnly` picture did not move at
all beyond tests that `58e05a0` skipped outright.

| Run | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| `Native` (default) — run 1 | 4599 | 4559 | **12** | 28 |
| `Native` (default) — run 2 (confirm) | 4599 | 4559 | **12** | 28 |
| `MONGODB_EF_NATIVE_ONLY=1` | 4599 | 2194 | 2377 | 28 |
| *pre-rebase on record (`229394f`), `Native`* | *4608* | *4589* | *0* | *19* |
| *pre-rebase on record (`229394f`), `NativeOnly`* | *4608* | *2194* | *2395* | *19* |

### The 12 `Native`-mode failures

All in `MongoDB.EntityFrameworkCore.SpecificationTests.Query.NorthwindGroupByQueryMongoTest`
(each ×2 for `async: True/False`):

| Method | Symptom |
|---|---|
| `Complex_query_with_group_by_in_subquery5` | `AssertTranslationFailed` got `BsonSerializationException` instead (duplicate element name `Expression` / creator-map on `MongoQuery<Customer,Customer>`) |
| `Join_GroupBy_Aggregate_in_subquery` | expected a translation failure; query now **succeeds returning 0 rows** (expected 133) |
| `Join_complex_GroupBy_Aggregate` | expected a translation failure; now **succeeds returning 0 rows** (expected 29) |
| `GroupJoin_complex_GroupBy_Aggregate` | expected a translation failure; now **succeeds returning 27** (expected 20) |
| `GroupBy_with_group_key_access_thru_nested_navigation` | `Assert.ThrowsAny<Exception>` — **no exception thrown** |
| `GroupBy_with_group_key_being_nested_navigation` | MQL baseline drift: expected `"OrderDetails."`, got `"OrderDetails.{ "$project" : { "_outer" : …"` |

Note the flavour: three of the six are *silently-wrong-data* cases (0 rows where 133/29 expected, 27 where 20
expected) surfacing through an `AssertTranslationFailed` override that no longer holds. Those are not merely
re-baselining — the driver 3.10 LINQ provider now *accepts* join-over-group shapes it used to reject, and
returns wrong answers. This is the same family as the `CSHARP-6017` skips `58e05a0` added; these six methods
look like the same bug reaching the branch through a path upstream did not exercise.

### Proof it is branch-specific, not an upstream red

`NorthwindGroupByQueryMongoTest` run at `upstream/main` `58e05a0` in a throwaway detached worktree
(since removed):

```
Passed! - Failed: 0, Passed: 509, Skipped: 4, Total: 513
```

Green. The same class on `365391f` has 12 `Native` failures and 208 `NativeOnly` failures.

### Reconciliation of the totals (exact, no residue)

`58e05a0` added exactly **9** `[ConditionalTheory(Skip = …)]` attributes
(`git show 58e05a0 -- tests/ | grep -cE '^\+.*Skip *= *"'` → 9: 6× CSHARP-6017, 2× CSHARP-6126, 1× EF-352).
A skipped `[ConditionalTheory]` collapses its **2** executed `IsAsyncData` cases into **1** `NotExecuted` trx
entry. Therefore:

- total: 4608 − 9 = **4599** ✔
- skipped: 19 + 9 = **28** ✔
- executed: 4589 − 18 = 4571; minus the 12 new failures → **4559 passed** ✔
- `NativeOnly` failed: 2395 − 18 = **2377** ✔ (all 18 removed cases were `NativeOnly`-failing)
- `NativeOnly` passed: **2194**, byte-identical to pre-rebase ✔

Both runs covered an **identical 4599-name set** (verified by name-set equality, not counts). All 12
`Native` failures are also `NativeOnly` failures (`2365 + 12 = 2377`).

### Recipe verification

§7.4's recipe still works verbatim. `MONGODB_EF_NATIVE_ONLY=1` still flips the mode exactly as documented —
`tests/…/SpecificationTests/Utilities/MongoTestStore.cs:42-47`:

```csharp
public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
    => builder.UseMongoDB(TestServer.Client, Name,
        Environment.GetEnvironmentVariable("MONGODB_EF_NATIVE_ONLY") == "1"
            ? o => o.UseQueryMode(MongoQueryMode.NativeOnly)
            : null);
```

### Confidence / what would falsify this

- **High** on the 12 failures: two independent runs, identical sets, deterministic assertion failures (not
  timeouts or ordering), plus a green control at `upstream/main`.
- **High** on the arithmetic reconciliation: every figure lands exactly, including the unchanged `2194`.
- **A real gap, stated plainly:** I could **not** compare pre-rebase vs post-rebase *by test name*, because
  **no trx was retained from the `229394f` sweep**. The doc's process lesson about un-recheckable claims bit
  this spike directly. The exact arithmetic plus the unchanged `2194` makes set-identity very likely but
  does not prove it. That gap is now closed going forward: the three trx above are retained.
- Falsified by: a third `Native` run showing a different failure set; or by finding that
  `NorthwindGroupByQueryMongoTest.cs` was modified after `229394f` in a way that explains the 12 without the
  driver bump (I checked — the branch's version of these overrides asserts the same thing upstream's does).
- Build hygiene: the SpecificationTests assembly used by all three sweeps was built at 10:37 from a clean
  tree; the concurrent agent's `src/` rebuild landed at 10:46, **after** the last of my runs (10:42). No
  contamination.

---

## B2. The "54-test reference-nav bucket"

### Direct answer

**The number 54 is exactly right. The label on it is wrong, and wrongly steers this work stream.**

The bucket reproduces at exactly **54** post-rebase (30 `'Orders'` + 24 `'OrderDetails'`). But
**not one of the 54 is a single-level reference `Include`.** They are:

- **48 multi-level Includes** (`ThenInclude` chains), and
- **6 filtered Includes**.

The exception they raise says so itself — `MongoSelectLowerer.AppendLookupStages` (`MongoSelectLowerer.cs:275`):

```
Native pipeline does not support lookup for navigation '<Nav>'
(only single-level reference and single-level collection includes).
```

That is the guard for everything **except** single-level. Labelling it "reference `Include` / navigation
lookups" reads as "this is the reference-Include bucket", and it is not. **A single-level reference-`Include`
slice would move 0 of these 54.**

Single-level reference `Include` never reaches that guard at all. It nav-expands to a `LeftJoin`, and joins
are absent from `IsNativeRepresentableSlotOperator`, so it routes to `Fallback` via the `NativeSlotPopulator`
catch-all and lands in the generic **"query not natively representable"** bucket. §9.1 of the status doc
describes this correctly ("built but dormant … nav-expands to a `LeftJoin` the gate treats as non-native"), and
`src/…/Query/AGENTS.md:29` says the same. **§7.2's row label contradicts §9.1 and `AGENTS.md`; §7.2 is the
one that is wrong.**

### Attribution sanity-check (6 samples, 4 classes)

Every sample carried the identical stack: `MongoSelectLowerer.AppendLookupStages` line 276 →
`MongoSelectLowerer.Lower` → `MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline` →
`TryBuildNativeFactory`. No misattribution *of the cause* — the cause really is a lookup gate. The
misattribution is of the **shape**.

Shapes read from the EF Core test-base source, not inferred from names:

| Test | Actual query | Verdict |
|---|---|---|
| `Include_list` | `.Include(p => p.OrderDetails).ThenInclude(od => od.Order)` | multi-level |
| `Include_collection_then_reference` | `.Include(…OrderDetails).ThenInclude(…Order)` | multi-level |
| `Include_collection_then_include_collection` | `Orders` → `OrderDetails` | multi-level |
| `Include_collection_then_include_collection_then_include_reference` | 3 levels | multi-level |
| `Include_multi_level_collection_and_then_include_reference_predicate` | `OrderDetails` → `Product` | multi-level |
| `Filtered_include_with_multiple_ordering` | `Include(c => c.Orders.OrderBy(…))` | filtered |

### Re-derived bucket table (rules stated so the number is reproducible)

§7.2 notes its own script buckets on `Assert.Throws` first. I ran **both** orderings
(`scripts/bucket.py`, rules in its docstring):

**Ordering A — cause-first** (NTNSE reason text wins over assertion markers). The principled one:

| Count | Bucket |
|---:|---|
| 1118 | query not natively representable |
| 1042 | projection long tail (non-entity result) |
| 66 | `ArgumentOutOfRangeException` (shaper/index) |
| **54** | **reference-nav `$lookup`** (in fact: multi-level + filtered Include) |
| 48 | `Assert.Throws` exception-type mismatch |
| 26 | `Assert.Contains` message-text mismatch |
| 13 | non-constant regex (EF-247) |
| 8 | `Not` renderer gap |
| 2 | other (`Throws_on_concurrent_query_first`) |
| **2377** | total |

**Ordering B — assertion-first** (reproduces §7.2's method):

| Count | Bucket | §7.2 on record | Δ explained by |
|---:|---|---:|---|
| 887 | projection long tail | 873 | +14 |
| 802 | query not natively representable | 794 | +8 |
| 519 | `Assert.Throws` exception-type mismatch | 559 | −40 (of which 18 = the new skips) |
| 66 | `ArgumentOutOfRangeException` | 66 | — |
| **54** | **reference-nav `$lookup`** | **54** | **unchanged** |
| 26 | `Assert.Contains` | 26 | — |
| 13 | non-constant regex | 13 | — |
| 8 | `Not` renderer gap | 8 | — |
| 2 | other | 2 | — |
| 2377 | total | 2395 | −18 |

Note the 54 bucket is **stable across both orderings** — it does not overlap the assertion buckets at all,
which is why the figure is robust.

### §7.1 per-class table

**22 of 24 rows reproduce exactly.** The two that move are fully explained by the 9 new skips:

| Class | §7.1 on record | Measured | Δ |
|---|---:|---:|---:|
| `NorthwindWhereQueryMongoTest` | 226 | 222 | −4 (2 CSHARP-6126 tests × 2 cases) |
| `NorthwindJoinQueryMongoTest` | 78 | 64 | −14 (7 skipped tests × 2 cases) |

−4 + −14 = −18 ✔.

### The Include-class shape breakdown (the question that actually matters)

§7.1 attributes 560 failures to the five "Include-flavoured" classes. **498 of those are in the four
`NorthwindInclude*` classes** (the fifth, `NorthwindNavigationsQueryMongoTest`'s 62, are navigation
*projection/predicate* tests, not `Include` tests at all). Broken down by **measured** shape — read from
`NorthwindIncludeQueryTestBase.cs`, treating a dotted `.Include(o => o.Order.Customer)` as two levels (the
pre-`ThenInclude` spelling; the first version of my classifier got this wrong and undercounted multi-level
by 72 cases):

| Cases | Shape | In scope for a single-level reference slice? |
|---:|---|---|
| 152 | single-level **collection** only | No — collection Include is already native; these fail for other reasons (client filter, `Last`, joins, `Contains` lists, projections) |
| 140 | **multi-level** (`ThenInclude`) | No |
| **96** | **single-level REFERENCE, 1 nav** | **Yes — but see split below** |
| 72 | **multi-level** (dotted `Include` path) | No |
| 16 | single-level reference, 2+ **sibling** navs | Adjacent (needs N parallel `$lookup`s) |
| 16 | single-level **mixed** reference + collection | Adjacent (needs ref + collection lookups composed) |
| 6 | **filtered** Include | No |
| **498** | total | |

Multi-level therefore accounts for **212** of the 498 — by far the biggest block, and none of it in scope.

The 96 single-level-reference cases split by failure cause:

- **56** — `query not natively representable` (the pure `LeftJoin` decline). **These are the genuinely
  in-scope tests.**
- **40** — `projection long tail`. Five methods (`Include_reference_when_projection`,
  `Include_reference_when_entity_in_projection`, `Include_with_complex_projection`,
  `Include_where_skip_take_projection`,
  `Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression`) that also need the
  non-entity projection long tail. A reference-Include-only slice does **not** move them.

Outside the four Include classes, 24 further `Include`-named cases fail under `NativeOnly`; the clearest
in-scope-adjacent ones are `NorthwindQueryFiltersQueryMongoTest.Included_many_to_one_query` and `…_query2`
(4 cases — many-to-one is a reference nav, wrapped in a query filter).

### Confidence / what would falsify this

- **Very high** on "54 reproduces" and "none of the 54 is a single-level reference Include": the count is
  exact under two independent bucketings, and the shapes were read from EF Core source, cross-checked
  against a stack trace naming the exact guard line, whose own message states the constraint.
- **Medium-high** on the shape breakdown. Two caveats: (a) the EF Core clone I read shapes from is
  `main`/11.0.0 while the provider builds against 10.0.8 — these are long-standing tests so bodies are
  almost certainly unchanged, and every one of the 66 failing methods was found in that file (no
  `NOT-FOUND`), but I did not diff the 10.0.8 sources; (b) `Include_reference_distinct_is_server_evaluated`
  (Distinct) and `Include_reference_single_or_default_when_no_result` (SingleOrDefault) and
  `Include_empty_reference_sets_IsLoaded` (`AssertFirst`) each compose an extra operator over the
  reference Include, so a minimal slice might not reach all three.
- Falsified by: reading the 10.0.8 test bodies and finding a different shape; or by an implementation that
  gates on "pending navigation is a reference" in a way that also happens to admit multi-level chains.

---

## B3. Axis 2 — what breaks on re-baselining when reference `Include` goes native

### Direct answer

**All 56 in-scope cases carry a non-empty `AssertMql` baseline, so axis 2 == axis 1 for this slice: 56
re-baselines, one per moved case.** Every one of those 56 baselines contains `_outer`, i.e. the driver's
`LeftJoin` document shape — which is exactly what a native `$lookup`/`$unwind` implementation replaces.

The larger number to be aware of: **382** currently-fallback-served spec tests carry an `_outer` baseline.
That is the axis-2 exposure of the *whole* join / reference-nav work stream, not of this slice — but it is
the population a gate widening could accidentally reach.

### How MQL assertions work in this suite

`AssertMql(...)` is generated, not hand-written. `TestMqlLoggerFactory.AssertBaseline` compares captured MQL
against the checked-in string; with `EF_TEST_REWRITE_BASELINES=1` (or `TRUE`) it rewrites the `AssertMql(...)`
call **in place** from the captured MQL and still reports the test failed (that's the signal a rewrite
happened) — then rebuild and re-run without the var. It is **data-gated by construction**: `AssertMql` is the
last call in an override, after `await base.…`, so a test that fails its data assertion never reaches it.
Consequence: for the 56 in-scope tests, the data assertion must pass natively **first**; only then does the
baseline rewrite become available.

### Measured baseline exposure

| Population | Cases | With non-empty `AssertMql` | With `$lookup` | With `_outer` |
|---|---:|---:|---:|---:|
| `Pass(Native)` ∧ `Fail(NativeOnly)` — everything the fallback serves today | 2365 | 2053 | 656 | **382** |
| **Strict in-scope: single-level ref, 1 nav, non-projection** | **56** | **56 (100 %)** | 56 | **56 (100 %)** |
| Adjacent: 2+ sibling single-level refs | 16 | 16 | 16 | 8 |
| Adjacent: mixed single-level ref + collection | 16 | 16 | 16 | 16 |

`_outer` by class across the fallback set: `Join` 56, `EFPropertyInclude` 52, `Include` 52, `StringInclude`
52, `Miscellaneous` 42, `IncludeNoTracking` 40, `Navigations` 28, `GroupBy` 14, `AsNoTracking` 10, `Select`
10, `QueryFilters` 6, `KeylessEntities` 4, `Where` 4, `AggregateOperators` 4, `AsTracking` 3,
`ChangeTracking` 3, `SetOperations` 2 — total 382.

### The residual axis-2 risk that a trx diff cannot predict

The `Select_All` class of miss was: a test that is `NativeOnly`-**failing** *and* whose `Native`-mode MQL
baseline the slice changes. Every such test for this slice is inside the 56 and is accounted for above.

The category that **cannot** be predicted from these trx files is the other one: a test that is currently
`NativeOnly`-**passing** (already native) whose `Native` MQL shifts because the slice edits shared lowering
code (`AppendLookupStages`, `MongoSelectLowerer.Lower`, projection binding). No static analysis of the
baseline finds those. The only reliable detector is **re-running the `Native` sweep after the slice and
diffing the failure set against `native.trx` retained here** — which is now possible, and was not for the
previous slices.

### The `AssertTranslationFailed` hard constraint — verdict: NOT a blocker for this slice

`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md:147` records the constraint. **Verified exactly, and the
recorded counts are correct:**

| Method | Class | Cases on EF10 | `Native` | `NativeOnly` |
|---|---|---:|---|---|
| `Select_collection_navigation_simple` | `NorthwindNavigationsQueryMongoTest` | 2 | Passed | Passed |
| `Select_collection_navigation_simple_followed_by_ordering_by_scalar` | `NorthwindNavigationsQueryMongoTest` | 2 | Passed | Passed |
| `Select_collection_navigation_multi_part` | `NorthwindNavigationsQueryMongoTest` | 2 | Passed | Passed |
| `Select_collection_navigation_multi_part2` | `NorthwindNavigationsQueryMongoTest` | 2 | Passed | Passed |
| `Order_by_length_twice_followed_by_projection_of_naked_collection_navigation` | `NorthwindFunctionsQueryMongoTest` | **0** | — | — |
| | | **8 on EF10** | | |

The fifth class is wholly `#if EF8 \|\| EF9` (`NorthwindFunctionsQueryMongoTest.cs:16`), so it contributes 0
cases on EF10 and 2 each on EF8/EF9 → **8 on EF10, 10 across the three majors. Exactly as recorded.**

**Assessment — it does not collide with a single-level reference-`Include` slice:**

1. All four EF10 methods pass under **`NativeOnly`** as well as `Native`. Under `NativeOnly` no
   `NativeTranslationNotSupportedException` is raised, so the provider's native **gate** is not what makes
   them fail-as-expected. Nothing about them depends on the fallback.
2. Their overrides carry **empty** `AssertMql()` — no MQL is ever captured, so the failure happens before any
   pipeline is emitted. Nothing to re-baseline, and nothing for the lowerer to change.
3. The shape is a **bare reference-COLLECTION array projection** (`Select(c => c.Orders)`), which is disjoint
   from reference `Include` (`Include(o => o.Customer)` — a *reference* nav, materialized by `$lookup` +
   `$unwind`, not projected as an array). Note the sibling
   `Select_collection_navigation_simple2` (`new { c.CustomerID, Count = c.Orders.Count }`) already goes
   native with a real `$lookup` baseline, so collection-nav `$lookup` machinery coexisting with these four
   is already demonstrated.

**These 8 cases are a blocker for the later reference-collection *bare array projection* slice** (the
SP3-wide bare-projection boundary), **not for this one.** They should still be asserted unchanged in the
slice's verification, precisely because `EF_TEST_REWRITE_BASELINES` cannot repair an exception-type
assertion — but they are not expected to move.

### Confidence / what would falsify this

- **High** on the 56/56 baseline exposure and the 382 figure: mechanical extraction from the override
  sources, scripted and re-runnable.
- **High** on the 8/10 `AssertTranslationFailed` counts: read directly out of both trx files.
- **Medium** on the collision verdict. It rests on reference-`Include` `$lookup` work not touching the
  bare-collection-array projection path. Falsified if the slice's implementation routes reference Include
  through a shared "nav leaf projection" mechanism that also starts accepting `Select(c => c.Orders)` — in
  which case all 8 flip `Passed → Failed` on the exception **type** and cannot be re-baselined. Cheap
  guard: assert those 4 methods still pass, in both modes, as an explicit slice exit criterion.

---

## The honest delta

**A single-level reference-`Include` native slice moves 56 spec-test cases on axis 1, and requires 56 MQL
re-baselines on axis 2. It moves 0 of the 54 tests the status report attributes to it.**

| | Best estimate | Plausible band | Basis |
|---|---:|---|---|
| **Axis 1** (`NativeOnly` Failed → Passed) | **56** | 56 – 100 | 7 methods × 4 `NorthwindInclude*` classes × 2 async = 56 strict. +16 if sibling multi-reference Includes are included, +16 if mixed ref+collection, +~10 adjacent outside the Include classes (clearest: `QueryFilters.Included_many_to_one_query`/`_query2`, 4). |
| **Axis 2** (currently-passing `Native` tests needing re-baseline) | **56** | 56 – 100, and see caveat | Identical set: 100 % of the axis-1 movers carry a non-empty `AssertMql` baseline containing `_outer`. Caveat: shared-lowering changes could shift baselines of already-native tests; unpredictable from trx, detectable only by diffing a post-slice `Native` sweep against the retained `native.trx`. |
| **The 54-bucket** | **0** | 0 | Every one of the 54 is multi-level (48) or filtered (6). Zero overlap. |

**Uncertainty, stated:**

- The tight number is **56**; it is a *floor-and-likely-actual*, not a ceiling. Three of the seven methods
  compose an extra operator over the Include (`Distinct`, `SingleOrDefault`, `First`), so a deliberately
  minimal slice might land **32–56** rather than 56.
- The band's upper end (100) assumes the slice also handles N parallel reference lookups and composes with
  the already-native collection lookup. Those are genuinely separate capabilities; treat 100 as "if the
  slice is scoped generously", not as the expected outcome.
- **The much larger prize is adjacent but out of scope for a single slice.** 212 of the 498 Include-class
  failures are multi-level, and the guard that rejects them is a *single* `else throw` in
  `AppendLookupStages`. If the reference-Include slice generalizes the lookup lowerer to chained navigation
  paths, the reachable set is ~268 (56 + 212) rather than 56. That is the design question worth deciding
  deliberately, and it is the honest reason the "54" framing was misleading in both directions: it
  overstated the reference-Include slice and understated what the same code area could unlock.
- **Reference-nav access (not Include) is a separate and bigger population** — 382 fallback-served tests
  carry an `_outer` driver-`LeftJoin` baseline, including 56 in `NorthwindJoinQueryMongoTest`, 42 in
  `Miscellaneous`, 28 in `Navigations`. Whether the slice's gate change is scoped to *pending Include
  navigations* or to *nav-expansion `LeftJoin` generally* is the single biggest determinant of its blast
  radius, on both axes.

**Before any of this: the 12 `Native`-mode `NorthwindGroupByQueryMongoTest` failures are a live regression on
the branch tip and should be triaged first.** Three of them are silently-wrong-data cases from driver 3.10,
not baseline drift, so they are not a `EF_TEST_REWRITE_BASELINES` fix.

---

## Appendix A — the 54 "reference-nav `$lookup`" cases, by class

Each method listed appears with both `async: True` and `async: False`.

**`NorthwindIncludeQueryMongoTest` (14)**, **`NorthwindEFPropertyIncludeQueryMongoTest` (14)**,
**`NorthwindIncludeNoTrackingQueryMongoTest` (14)** — identical method sets:

| Method | Shape |
|---|---|
| `Filtered_include_with_multiple_ordering` | filtered (`Include(c => c.Orders.OrderBy(…))`) |
| `Include_collection_then_include_collection` | multi-level `Orders → OrderDetails` |
| `Include_collection_then_include_collection_predicate` | multi-level |
| `Include_collection_then_include_collection_then_include_reference` | 3-level |
| `Include_collection_then_reference` | multi-level `OrderDetails → Order` |
| `Include_list` | multi-level `OrderDetails → Order` |
| `Include_multi_level_collection_and_then_include_reference_predicate` | multi-level `OrderDetails → Product` |

**`NorthwindStringIncludeQueryMongoTest` (12)** — the same list minus
`Filtered_include_with_multiple_ordering` (no string-based filtered-Include equivalent).

---

## Appendix B — the 56 strictly in-scope cases (axis 1 *and* axis 2)

7 methods × 4 classes × 2 async values. Classes: `NorthwindIncludeQueryMongoTest`,
`NorthwindEFPropertyIncludeQueryMongoTest`, `NorthwindStringIncludeQueryMongoTest`,
`NorthwindIncludeNoTrackingQueryMongoTest`.

| Method | EF Core query | `_outer` baseline |
|---|---|---|
| `Include_reference` | `Set<Order>().Where(o => o.CustomerID.StartsWith("F")).Include(o => o.Customer)` | yes |
| `Include_reference_alias_generation` | `Set<OrderDetail>().Where(od => od.OrderID % 23 == 13).Include(o => o.Order)` | yes |
| `Include_reference_with_filter` | `Where(…).Include(o => o.Customer)` | yes |
| `Include_reference_with_filter_reordered` | `Include(o => o.Customer).Where(…)` | yes |
| `Include_reference_distinct_is_server_evaluated` | `.Include(o => o.Customer)` + `Distinct` | yes |
| `Include_reference_single_or_default_when_no_result` | `.Include(o => o.Customer)` + `SingleOrDefault` | yes |
| `Include_empty_reference_sets_IsLoaded` | `Set<Employee>().Include(e => e.Manager)` via `AssertFirst` | yes |

## Appendix C — adjacent (probable follow-on, not counted in the 56)

| Cases | Method(s) | Shape |
|---:|---|---|
| 8 | `Include_multiple_references` | `Include(o => o.Order).Include(o => o.Product)` — 2 sibling single-level refs |
| 8 | `Include_reference_dependent_already_tracked` | 2× single-level `Customer` |
| 8 | `Include_reference_and_collection` | `Include(o => o.Customer).Include(o => o.OrderDetails)` |
| 8 | `Include_collection_and_reference` | `Include(o => o.OrderDetails).Include(o => o.Customer)` |
| 4 | `NorthwindQueryFiltersQueryMongoTest.Included_many_to_one_query`, `…_query2` | reference Include under a query filter |
| 2 | `NorthwindAsNoTrackingQueryMongoTest.Include_reference_and_collection` | as above, no-tracking |

## Appendix D — reproduction

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider   # tip 365391f, MONGODB_URI and ATLAS_URI both unset
R=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/323eba4f-8228-4bc2-ae7f-40ed6677ddfe/scratchpad
P=tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj

dotnet build $P -c "Debug EF10"
dotnet test  $P -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx"     --results-directory $R/specsweep-365391f/
MONGODB_EF_NATIVE_ONLY=1 \
dotnet test  $P -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory $R/specsweep-365391f/

cd $R/scratchpad 2>/dev/null || cd $R
python3 scripts/bucket.py          specsweep-365391f/nativeonly.trx   # bucket tables + per-class table
python3 scripts/classify_include.py specsweep-365391f/nativeonly.trx  # Include shape breakdown
```

`git status` is clean at finish; `HEAD` is unchanged at `365391f`. The only untracked file in the tree
(`tests/…/UnitTests/Query/NativeTranslation/ZzSpikeAReferenceIncludeProbe.cs`) belongs to the **concurrent
Spike A agent**, not to this spike — left alone deliberately. The temporary `upstream/main` worktree used for
the control run has been removed and `git worktree prune`d.
