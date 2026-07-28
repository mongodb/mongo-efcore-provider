# EF-322 step 3a — Task 0 spike findings

*Run 2026-08-06 against `EF-322-step3a` @ `69a65d31`. Two throwaway worktrees (a base at `69a65d31` and a
head carrying the complete change) were created under this agent's own scratchpad and have been **removed**
(`git worktree list` verified — only the three pre-existing `.claude/worktrees/agent-*` worktrees belonging to
other sessions remain, untouched). Nothing under `src/` or `tests/` in the main tree was modified; the only
change to the main tree is this file, uncommitted.*

**Tagging.** **MEASURED** = I executed it this session (commands in §9). **READ** = established by reading
source at `69a65d31`. **INFERRED** = drawn from MEASURED/READ facts but not observed. **UNVERIFIED** = not
established. The design doc (`2026-08-06-step3a-bare-projection-design.md`) is authoritative on intent; the
numbers below are authoritative on fact, and **five of its claims are refuted**.

---

## 0. Executive summary — read this first

| | |
|---|---|
| **The design's headline number is wrong in both halves.** | Tier 1 delivers **74**, not 78. Tier 2 delivers **+6**, not +22. Total **80**, not 100. MEASURED, by failing-test-name SET on both axes. |
| **The complete change AS DESIGNED introduces SILENT WRONG DATA under the DEFAULT `Native` mode.** | A bare projection composed with a `Where` the native *renderer* cannot emit (e.g. a **parameterized** `string.StartsWith` — an entirely ordinary shape) returns `null` for a nullable scalar and an **empty collection** for a bare owned array, with no exception, where the base tree returns correct values. §2. |
| **The design's central safety claim — §3.3, "the measured-worse state is unrepresentable" — is REFUTED.** | Two independent live shapes reach emit-admitted / read-declined. §3. |
| **The §6.2 fail-loud invariant caught nothing.** | It never fired once across six spec-suite runs and six functional-probe runs, including the runs that produced silent wrong data. It is checking the wrong pair of facts. §3.2. |
| **The §3.1 carrier code does not compile as written.** | `Dictionary<string?, string>` cannot take a `null` key; `AddProjectionAliasOverride(null, …)` and `ContainsKey(null)` both throw `ArgumentNullException`. A sentinel key is required. §3.1. |
| **There is a FOURTH alias-derivation site.** | `MongoMixedProjectionBindingRemovingExpressionVisitor.cs:91`. It did not need changing for any measurement to come out right, but the design's inventory of "three sites" is incomplete as an inventory. §4. |
| **There IS a shippable 3a, and it needs one file the design does not touch.** | Tier 1 + tier 2 + a **leaf-kind-conditional strip of the pushed-down bare `Select` on `CompileShapedQuery`'s native-factory-failure fallback**. MEASURED: **80** `NativeOnly` wins, **0** regressions, **1** residual default-`Native` failure (an exception-*message* change on an already-unsupported shape), no silent wrong data anywhere probed. §6. |
| **Recommendation on tier 2: KEEP it**, but only alongside that fallback fix, and only if the owner accepts one measured behaviour change to the escape hatch. §7. |
| **Cost the design did not budget for: 78 `AssertMql` baselines across 13 spec files must be re-written.** The design's Task-2 verification demands "**zero** `AssertMql` baseline diffs (any diff is a finding …)". That criterion is unmeetable for tier 1 and must be replaced. §5.4. |

---

## 1. Baseline verification (prerequisite)

MEASURED, base worktree at `69a65d31`, EF10 specification suite, both `MONGODB_URI` and `ATLAS_URI` unset:

| Mode | Passed | Failed | Skipped |
|---|---:|---:|---:|
| default `Native` | **4593** | **0** | 17 |
| `MONGODB_EF_NATIVE_ONLY=1` | **2352** | **2241** | 17 |

Exactly the figures the prompt and the design cite. Failing test names are unique (2241 distinct names for
2241 failures), so name-set comparison is well-defined. All comparisons below are by NAME SET, never by count
alone, and every failure is additionally bucketed by message with `Assert.Throws`/`ThrowsAny` classified
**first**.

**Inertness control (MEASURED).** With the emit-side arm switched off (`SPIKE_3A=0`) but all three read-side
edits present — site A's alias derivation, site C's alias derivation, and the §6.2 invariant — the
`NativeOnly` failing-name set is **byte-identical to base** (2241/2352/17, `diff` empty). So the design's
sequencing claim (§2.3, §8: "Task 1 lands the read side while it is inert") is **VERIFIED**.

---

## 2. Q2 — does the complete change deliver 78–100? **NO. REFUTED.**

### 2.1 The complete change as designed

MEASURED, head worktree, tier 1 + tier 2 + both narrowings, **no** other changes:

| Axis | Base | Head | Delta |
|---|---:|---:|---|
| default `Native` failures | 0 | **85** | **+85 — the design predicted 0** |
| `NativeOnly` failures | 2241 | 2239 | −2 |

`NativeOnly` Failed→Passed = **2**. Passed→Failed = **0**.

So on a naive reading the complete change delivers **2**, not 78. That reading is wrong, and the reason is
the single most important measurement instrument in this task:

> **On the `NativeOnly` axis the win is invisible until the MQL baselines are re-written.** The spec suite
> runs `AssertMql` under `MONGODB_EF_NATIVE_ONLY=1` too. A case that stops declining now *reaches* its
> committed MQL assertion and fails there instead. Bucketing the still-failing names by message transition:
>
> | Cases | base message | head message |
> |---:|---|---|
> | **76** | `NativeTranslationNotSupportedException` | MQL string diff |
> | 6 | `NativeTranslationNotSupportedException` | `NativeTranslationNotSupportedException` |
> | 2 | `Assert.Throws` mismatch | `ArgumentOutOfRangeException` |
> | 1 | `Assert.Throws` mismatch | `Assert.Contains` |
>
> 76 + 2 outright = **78**. The spike's 78 is reproducible — but only as "stopped declining", not as "green".

### 2.2 After re-baselining the MQL (`EF_TEST_REWRITE_BASELINES=1`, rebuild, re-run)

| Axis | Base | Head (re-baselined) |
|---|---:|---:|
| default `Native` failures | 0 | **7** |
| `NativeOnly` Failed→Passed | — | **80** (0 Passed→Failed) |

78 of the 85 default-`Native` failures were pure MQL churn across 13 spec files. **7 are genuine defects**,
and 6 of them are one defect (§2.4).

### 2.3 Tier split — the design's family attribution is wrong

MEASURED by the same message-transition instrument, tier 2 switched off:

| Variant | "stopped declining" | outright Failed→Passed | total win |
|---|---:|---:|---:|
| Tier 1 only | 72 | 0 | **72** |
| Tier 1 + tier 2 | 76 | 2 | **78** |
| Tier 1 only **+ the §6 fallback fix**, re-baselined | — | — | **74** |
| Tier 1 + tier 2 **+ the §6 fallback fix**, re-baselined | — | — | **80** |

**Tier 2's whole contribution is 6 cases**, and they are identifiable by name: 4 ×
`NorthwindSelectQueryMongoTest.Projecting_count_of_navigation_which_is_generic_collection` /
`…_generic_list` (a bare *reference*-collection `.Count`) and 2 ×
`NorthwindSelectQueryMongoTest.Explicit_cast_in_arithmetic_operation_is_preserved` (a bare arithmetic leaf).

**Verdict:** the design's §0/§10 "**78** from tier 1, **up to 100** with tier 2" is **REFUTED**. The measured
figures are **74 / +6 / 80**. The spike's family-B sizing (56 in the bucket, 22 sole-cause) does not convert
to spec wins at anything like that rate, because most family-B bodies are constants, captured parameters, or
leaf kinds `TryTranslateLeaf` still declines.

**Win by family (MEASURED, the 80, by test method):** `Include_reference_when_projection` 8,
`Include_collection_when_projection` 8, `VectorSearch_with_projection` 4, `Projecting_count_of_navigation_*`
4, and 2 each of `Where_projection`, `Where_primitive`, `Union_Select`, `Take_subquery_projection`,
`Take_simple_projection`, `Select_scalar_primitive_after_take`, `Select_scalar_primitive`, `Select_scalar`,
`Select_project_filter`, `Select_project_filter2`, `Select_into`, `Select_Order`, `Select_OrderDescending`,
`Queryable_simple_anonymous_projection_subquery`, `Projection_when_null_value`, `OrderBy_scalar_primitive`,
`OrderBy_multiple`, `OrderBy_ThenBy`, `OrderBy_ThenBy_same_column_different_direction`, `OrderBy_Select`,
`OrderBy_OrderBy_same_column_different_direction`, `OrderByDescending`, `OrderByDescending_ThenBy`,
`OrderByDescending_ThenByDescending`, `Explicit_cast_in_arithmetic_operation_is_preserved`,
`Concat_with_pruning`, `Anonymous_projection_AsNoTracking_Selector`, plus 1 each of `Tag_on_scalar_query` and
`Multiple_entities_can_revert`.

**VectorSearch (MEASURED):** the `NativeOnly` `VectorSearch` residual moves **20 → 16**, and the remaining 16
are exactly 4 `VectorSearch_with_complex_pre_filter` (EF-382) + 12 entity-leaf cases (3b). The VectorSearch
design's own claim is **VERIFIED by direct measurement**.

### 2.4 The 6 genuine default-`Native` defects, and why they matter more than their count

All six are `InvalidOperationException: Document element '_id' is missing for required non-nullable property
'CustomerID'`, from `BsonBinding.GetPropertyValueAtElement`, in
`NorthwindMiscellaneousQueryMongoTest.Comparing_to_fixed_string_parameter` and
`NorthwindQueryFiltersQueryMongoTest.Projection_query` / `Projection_query_parameter`. Every one of those
queries is a bare projection whose `Where` predicate is a **parameterized** `string.StartsWith` — a shape the
native *renderer* refuses (`"Only constant regex terms are natively representable"`).

**Mechanism, MEASURED end to end.** `Route == NativeRoute.Projection` is decided at translation time, so
`VisitProjectedQuery` takes the native branch and `CompileShapedQuery` builds the **alias-addressed DOM
shaper**. `TryBuildNativeFactory` then fails (renderer decline, caught because mode ≠ `NativeOnly`), so
`nativeFactory == null` and the runtime helper translates `CapturedExpression` — **including the bare
`Select`** — through the driver-LINQ bridge. The driver aliases a bare projection `_v`. Captured MQL:

```
aggregate([{ "$match" : { "Title" : { "$regularExpression" : … } } },
           { "$project" : { "_v" : "$Title", "_id" : 0 } }])
```

while the shaper reads element `Title`. The read misses.

> **This REFUTES the design's §4.2 table row** *"native-factory build failure under `Native` → the same
> alias-addressed DOM shaper, **over `aggregate([])`**"*. MEASURED FALSE: the fallback pipeline **keeps the
> pushed-down `$project`**, keyed by the driver's own `_v`. Tier 1's entire justification — "alias == document
> path, so the shaper is correct against a whole document too" — is therefore aimed at a row the fallback
> never hands it.

**And the six loud cases are the lucky ones.** MEASURED on a purpose-built ragged fixture (five rows: a
2-element array, an empty array, a **missing** `Posts`, an explicitly **BSON-null** `Posts`, a 1-element
array), query `Where(b => b.Title.StartsWith(prefix)).Select(<leaf>)` with `prefix` a captured local, default
`Native` mode, base vs. the complete change as designed:

| bare leaf | base | design as written | verdict |
|---|---|---|---|
| non-nullable `string` | `p2,p2` | **throws** `InvalidOperationException` | LOUD regression |
| **nullable `string`** | `n_p2,n_p2` | **`<null>,<null>`** | **SILENT WRONG DATA** |
| **nullable `int`** | `10,50` | **`<null>,<null>`** | **SILENT WRONG DATA** |
| **owned array** (`b.Posts`) | `[h1\|h2];[h9]` | **`[];[]`** | **SILENT WRONG DATA** |
| tier-2 `.Count` | `2,1` | `2,1` | correct — by accident (`_v` == the driver's own alias) |
| control: **constant** prefix | `p2,p2` | `p2,p2` | correct (native factory succeeds) |

**Control (MEASURED): the hazard is NOT pre-existing.** The same query with a **wrapped** projection
(`Select(b => new { b.Note })`, `new { b.Title, b.Posts }`, `new { b.Score }`, `new { N = b.Posts.Count }`)
returns correct values in all three modes at base **and** at head. The driver aliases a wrapped projection by
member name, which coincides with the native alias; only a **bare** body gets `_v`. So 3a introduces this,
and it introduces it for the shape the whole slice is about.

**Consequence for the slice's shape:** the 6-case spec signal is a floor, not a ceiling. Northwind's
`CustomerID` is non-nullable, which is the only reason those six are loud rather than silent. Any nullable or
collection-valued bare leaf in the same position is silent — and the design's tests (§9.3) would not have
caught it, because every one of them uses a constant-only `Where`.

---

## 3. Q1 — the four UNVERIFIED items, verbatim, with verdicts

Extracted verbatim from the design's §12 table (the two remaining rows are Task 4's, not Task 0's):

### 3.1 *"that a complete 3a turns the 78 green (the spike measured only the incomplete change)"* — **REFUTED**

Three independent reasons, all MEASURED:

1. **Nothing turns green without re-baselining.** 76 of the 78 become MQL string diffs (§2.1).
2. **The number is wrong.** Tier 1 = 72 (74 with the §6 fix), total 80, not 78/100 (§2.3).
3. **A complete implementation of §3+§4 as written also introduces silent wrong data** under default
   `Native` and 7 residual default-`Native` failures (§2.4). It is not merely incomplete; it is unsound.

**Also REFUTED as written: the §3.1 carrier code.** `private Dictionary<string?, string>?
_projectionAliasOverrides;` with `null` as the bare-body key, `.Add(memberName, alias)`, and
`ContainsKey(BareMemberKey→null)` — `Dictionary<TKey,TValue>` throws `ArgumentNullException` on a null key
and on `ContainsKey(null)`. The spike used a sentinel constant (`" bare"`). Trivial to fix, but it means
the design's code block cannot be typed in as-is.

### 3.2 *"that `CanPushDown` is true for every tier-2 shape, so `DriverLinq` never reads a `_v` alias"` — **the antecedent VERIFIED, the mitigation REFUTED**

MEASURED, bare `.Count` under explicit `DriverLinq` at head:

```
aggregate([{ "$project" : { "_v" : { "$size" : "$Posts" }, "_id" : 0 } }])
```

That is the driver push-down with the identity shaper, so `CanPushDown` **is** true and the provider's `_v`
alias **is** never read on that route. The antecedent holds.

But the mitigation it was supposed to buy does not:

- **It does not cover the route that actually reads the alias.** The dangerous route is not `DriverLinq`, it
  is default `Native` with a **late native-factory failure** (§2.4), which the design's §4.2 table
  mis-describes. `CanPushDown` is never consulted there.
- **The push-down itself is not safe.** MEASURED: on the ragged fixture the same `DriverLinq` query throws
  `MongoCommandException: The argument to $size must be an array, but was of type: missing`. The driver
  renders a **bare** `$size` with no `$ifNull`; native renders `{$size: {$ifNull: ["$Posts", []]}}` and
  answers `0`. See §3.3.

**Also: the §6.2 fail-loud invariant NEVER FIRED — not once**, across six spec-suite runs and six functional
probe runs, including every run that produced silent wrong data. It is structurally incapable of catching the
real divergence: it compares the override string against `Select.Projection`'s own aliases — two facts the
same code writes in the same block — while the divergence is between that alias and what the **driver**
emits, or between it and what the shaper is handed after a `Route` flip. **As specified it is a tautology
with a throw attached.**

### 3.3 *"the `DriverLinq` disposition of a bare `.Count` and a bare owned array on missing/null arrays"* — **MEASURED; the design's §7.4 prediction is VERIFIED**

Ragged fixture (populated / empty / **missing** / explicit **BSON null** / populated), all three modes:

| shape | mode | base | head (design as written) |
|---|---|---|---|
| `Select(b => b.Posts.Count)` | `Native` | `2,0,0,0,1` | `2,0,0,0,1` |
| | `DriverLinq` | `2,0,0,0,1` | **`MongoCommandException`** |
| | `NativeOnly` | throws (decline) | `2,0,0,0,1` |
| `Select(b => b.Posts)` | `Native` | `[h1\|h2];[];[];[];[h9]` | same |
| | `DriverLinq` | same | same |
| | `NativeOnly` | throws (decline) | same |

Base MQL for both, in **both** `Native` and `DriverLinq`: `aggregate([])` — the client-side fold the design
describes. **VERIFIED.** Head MQL: `Native` = `{$project: {_v: {$size: {$ifNull: ["$Posts", []]}}, _id: 0}}`;
`DriverLinq` = `{$project: {_v: {$size: "$Posts"}, _id: 0}}` (no `$ifNull` → the abort).

So design §7.4 is **exactly right**: tier 2 un-routes `Select(b => b.Posts.Count)` from its client-side fold
in every mode, and under explicit `DriverLinq` it inherits the already-documented wrapped-count divergence
(aborts on a missing/explicitly-null array where it used to answer `0`). This is the **only** measured cost of
tier 2 once the §6 fallback fix is in place, and it is confined to the explicit `DriverLinq` escape hatch.

The bare **owned array** is safe under `DriverLinq`: MEASURED `aggregate([])` (the mixed client-side shaper
over whole documents) and correct values for all four array states. That is precisely because tier 1's alias
is the element name — design §4.2/§4.3 **VERIFIED for this route**.

### 3.4 *"that §7.1 (trailing bare projection after a set op) is safe"* — **VERIFIED**

MEASURED, all four operators, all three modes, on a fixture where **two distinct entities share the same
`Title`** (so whole-entity dedup and projected-value dedup give different answers):

| query | `Native` | `DriverLinq` | `NativeOnly` |
|---|---|---|---|
| `Where(≤3).Union(Where(≥3)).Select(b => b.Title)` | `p2,p2,q0,r_missing,s_null` | identical | identical |
| `…Concat(…).Select(b => b.Title)` | `p2,p2,q0,r_missing,r_missing,s_null` | identical | identical |
| `…Intersect(…).Select(b => b.Title)` | `r_missing` | *(no oracle — throws)* | `r_missing` |
| `…Except(…).Select(b => b.Title)` | `p2,q0` | *(no oracle — throws)* | `p2,q0` |
| `Union(all, all).Select(b => b.Title)` | `p2,p2,q0,r_missing,s_null` | identical | identical |

All five answers are correct against the seed, and the **duplicated `p2`** directly confirms the design's
inferred mechanism: C2 dedups over whole entities **before** the trailing `$project`. `Intersect`/`Except`
have no driver-LINQ oracle (pre-existing) and their native answers are the arithmetically correct ones.
**Admit, as the design says. No narrowing needed.** The 2 spec wins here are `Union_Select` and
`Concat_with_pruning`.

---

## 4. Is there a fourth site? **YES — four, not three.**

`grep -rn 'Last?\.Name' src/` returns exactly three hits; a fourth site derives an alias by a different
route. Full inventory (READ at `69a65d31`):

| # | Site | Design lists it? |
|---|---|---|
| A | `MongoQueryExpression.ApplyProjection:108` — `AddToProjection(expression, projectionMember.Last?.Name)` | yes |
| B | `MongoProjectionBindingRemovingExpressionVisitor:82` — `if (projection.Alias is null) return DocParameter;` | yes (deliberately not edited) |
| C | `MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection:1004` — `arrayProjectionMember.Last?.Name` | yes |
| **D** | **`MongoMixedProjectionBindingRemovingExpressionVisitor:91` — `alias = projectionBindingExpression.ProjectionMember.Last?.Name`** | **no — listed only as "not changed, deliberately"** |

Site D is a genuine, independent fourth derivation of the same fact. READ, it is the `else` arm of a
`mappedExpression is ConstantExpression { Value: int }` test, i.e. it is reached only *before*
`ApplyProjection` has rewritten that member; the `if` arm reads `projection.Alias` and therefore inherits site
A for free. **MEASURED: no measurement in this spike required site D to change** — every mixed-path
(`DriverLinq`) result came out correct with site D untouched. But the design's inventory should say **four**,
and site D deserves an explicit "inherits site A, no edit needed, here is why" sentence rather than a bare
"not changed".

Two related READ facts the design does not record, both load-bearing for site D:

- The **mixed** visitor's `alias is null` branch does **not** return `DocParameter` unconditionally; it first
  tries `TryResolveFieldAccess` and only then falls back. That is the mechanism by which a bare scalar
  already materializes correctly on the mixed path today.
- Once site A supplies a non-null alias, the mixed visitor's tail reads
  `CreateGetValueExpression(_docParameter, alias, …)` — an alias read off the **whole** document. Correct for
  tier 1; a missing element for a tier-2 `_v`. MEASURED not reachable today (tier-2 shapes push down under
  `DriverLinq` with an identity shaper), but it is one `CanPushDown` change away from mattering.

**A half-moved site is exactly the failure mode this task exists to prevent, and it happened — twice — in a
form the design did not anticipate.** See §3 next.

---

## 5. Q4 / Q5 and the remaining measurements

### 5.1 Q4 — is a read-side alias mismatch silent? **YES. VERIFIED, twice, by mutation.**

**Mutation 1 (deliberate).** Force every bare alias to `_v` while leaving
`IsNativeArrayProjectionLeaf`'s `alias == GetContainingElementName()` conjunct in place — i.e. move the emit
side and leave the array shaper site behind. MEASURED, fully native (no fallback involved), MQL
`{$project: {_v: "$Posts", _id: "$_id"}}`:

```
Select(b => b.Posts)  [Native]     => [];[];[];[];[]        (correct: [h1|h2];[];[];[];[h9])
Select(b => b.Posts)  [NativeOnly] => [];[];[];[];[]
Select(b => b.Posts)  [DriverLinq] => [h1|h2];[];[];[];[h9]  (correct — different route)
```

**Five rows of silently empty collections, no exception, in the default mode.** The EF-358 coalesce turns the
missed `TypeAs` into an empty collection exactly as the design's §6.1 predicts. And the **§6.2 invariant did
not fire**, because the alias *is* present in `Select.Projection` — the disagreement is with the shaper's
independently-derived element-name expectation, which the invariant never looks at.

**Mutation 2 (accidental, and therefore the more convincing).** The design's own tier-1 alias, unmutated,
produces the same class of silence on the late-fallback route — §2.4's table. Nullable string → `null`,
nullable int → `null`, owned array → empty. No exception, default mode.

**Verdict: a read-side alias mismatch MUST be treated as silent (design §6.1 VERIFIED), and the design's
fail-loud invariant does NOT catch it (design §6.2 REFUTED).** Any replacement invariant has to compare the
shaper's expectation against *what the executed pipeline actually emits*, on every route the shaper can be
handed — which in practice means fixing the routes so there is only one answer (§6).

### 5.2 The design's §3.3 claim — **REFUTED, with a live shape**

§3.3 asserts the emit-gate-open / read-side-unchanged state is "not merely un-committed — it is
**unrepresentable**". MEASURED FALSE. With the §7.3 narrowing removed, `Select(o => o.Country).Distinct()`
reproduces the spike's finding-3 crash exactly:

```
NorthwindAggregateOperatorsQueryMongoTest.Distinct_Scalar   (async: False/True)
NorthwindAggregateOperatorsQueryMongoTest.OrderBy_Distinct  (async: False/True)
  System.ArgumentException : Expression of type
  'QueryingEnumerable`2[BsonDocument,BsonDocument]' cannot be used …
```

— four cases that **pass** at base under default `Native`. Mechanism (READ + MEASURED):
`TryBindDistinctFromProjection` clears `Projection`, installs a `Grouping`, and `Route` flips to
`NativeRoute.GroupBy`. Site A's `Route == NativeRoute.Projection` conjunct then reverts the alias to `null`,
site B returns `DocParameter`, and the shaper's return type becomes `BsonDocument` — while the emit side has
already committed. **The `Route == Projection` conjunct that §3.3 presents as the ordering safeguard is what
manufactures the divergence.** Two facts still exist; they are just written and read at different times.

The second instance is §2.4: emit committed, shaper alias-addressed, pipeline supplied by a different
component entirely.

### 5.3 Q5 — the two narrowings are both load-bearing, MEASURED

Dropping **both** (`SPIKE_NARROW=0`, everything else identical): default `Native` **85 → 109** failures,
**+24** Passed→Failed, and `NativeOnly` net **worse** (2239 → 2241).

| Shape | Effect of dropping the narrowing |
|---|---|
| **§7.3 bare `Distinct`** | 4 cases hard-fail (`ArgumentException`, above) in `Native` **and** `NativeOnly`, from a base state of passing under `Native`. The design says "It might well work" — **MEASURED FALSE.** |
| **§7.2 bare projected set-op operand** | 12 cases become MQL diffs (`Union_non_entity`, `Concat_non_entity`, `Select_Union`, `Union_over_OrderBy_Take1/2`, `Union_over_OrderBy_without_Skip_Take1/2`, `Union_over_column_column`) — **and `Intersect_non_entity` / `Except_non_entity` flip to `Assert.ThrowsAny() Failure: No exception was thrown`**, i.e. they now return an answer where the spec asserts a throw, on the two operators with **no** driver-LINQ oracle. This is the exact slice-8 hazard (`Union 1→2 / Intersect 1→0 / Except 0→1`), and the spec does not check the answer. |

**Both narrowings stay. Both need a tripwire test.** Functional probes with the narrowings ON confirm the
pre-3a disposition is preserved: `Union`/`Concat` bare operands return correct values under
`Native`/`DriverLinq` and throw `NativeTranslationNotSupportedException` under `NativeOnly`;
`Intersect`/`Except` bare operands hard-fail in every mode; `Select(b => b.Title).Distinct()` returns correct
values under `Native`/`DriverLinq` and declines under `NativeOnly`.

### 5.4 Other measured dispositions (probe, all three modes)

| Shape | Disposition at head | Design said |
|---|---|---|
| `Select(b => b.Title)` | native, `{$project: {Title: "$Title", _id: 0}}` | tier 1 ✔ |
| `Select(b => b.Id)` (single-prop PK) | native, `{$project: {_id: "$_id"}}` — well-formed, no `_id: 0` | tier 1 ✔ (§4.3 VERIFIED) |
| `Select(b => b.Tags)` (primitive collection) | native, correct for all four states | tier 1 ✔ |
| `Select(b => b.Posts)` (owned collection) | native, `{$project: {Posts: "$Posts", _id: "$_id"}}`, correct for all four states, matches `DriverLinq` | tier 1 ✔ |
| `Select(b => b.Home.City)` (owned hop, dotted) | **declines** — correct under `Native`/`DriverLinq`, throws under `NativeOnly` | declines ✔ (EF-362 tripwire) |
| `Select(b => 0)` / `Select(b => 5)` | **declines**, correct values via fallback | declines ✔ |
| `Select(b => b.Rank * 2)` | native (tier 2), correct in all modes | tier 2 ✔ |
| `Select(b => b.Posts.Count(p => …))` | native (tier 2), correct in all modes | tier 2 ✔ |
| `Select(b => b)` / `Where(…).Select(b => b)` | unchanged (never reaches the arm) | ✔ — see note |
| `Where/OrderBy/Skip/Take` then bare `Select` | native, correct | §7.5 ✔ |
| bare `Select` then `Count()` / `First()` | **succeeds natively**, correct | design §10 item 3 says 3d — **incidental widening, worth a test** |

*Note on the bare-entity control:* my fixture's whole-entity query fails on an unrelated shadow-FK seeding
gap (`PBlogId` absent from the raw-seeded owned elements), **identically at base and at head**, so the
positive control is inconclusive from this fixture. The spike's own MEASURED finding (bare entity already
native, zero `BAREBODY`/entity cases in the 881) stands unchallenged, and the arm added here cannot match a
bare `ParameterExpression` (READ). Design §9.3 test 14 should keep its own clean flat fixture.

**MQL churn (MEASURED):** tier 1 rewrites **78** `AssertMql` baselines across **13** spec files. The change is
uniformly `{$project: {_v: "$X", _id: 0}}` → `{$project: {X: "$X", _id: 0}}` (plus `$sort`-ordering diffs on a
handful and `$lookup`+`$size` on the four tier-2 reference-count cases). Per the versioning rubric emitted MQL
is not contract, so these are legitimate re-baselines — but the design's Task-2 acceptance criterion
("**zero** `AssertMql` baseline diffs … any diff is a finding") is **unmeetable** and must be restated as "the
diffs are exactly the `_v` → alias rename, enumerated".

---

## 6. The fix the design is missing, and the shippable shape

**The defect (READ + MEASURED).** `CompileShapedQuery` builds the shaper first and decides native-vs-driver
second. When `TryBuildNativeFactory` returns `null`, its runtime helper translates the **full**
`CapturedExpression` through `MongoEFToLinqTranslatingExpressionVisitor` and keeps the shaper that was built
for the *native* `$project`. Today that is harmless because every `Route == Projection` shape's native aliases
are member names, which is also what the driver picks. **A bare body is the first shape where the two
disagree** — the driver picks `_v`.

`StripPushedDownSelect` already exists in the same file (used by the mixed path, `:365`) and does exactly what
is needed.

### 6.1 Three candidate fixes, all MEASURED

| Variant | `NativeOnly` wins | default-`Native` residual after re-baseline | silent wrong data | bare array native |
|---|---:|---:|---|---|
| **A** — design as written (tier 1 doc-path alias + tier 2 `_v`) | 80 | **7** | **YES** (nullable scalar, nullable int, owned array) | yes |
| **B** — uniform `_v` for every bare leaf, array declined | 69 | 3 | no | **no** |
| **C** — A + strip the bare `Select` on the late fallback (unconditional) | 74 (tier 1 only) | 1 | no | yes — **but tier 2 throws** `Document element '_v' is missing but required` |
| **D** — A + strip **only for a tier-1 (path-addressable) bare leaf** | **80** | **1** | **no** | **yes** |

Notes on the ones that lose:

- **B** is what the design considered and rejected in §4.2. It is safe for scalars — MEASURED, the late
  fallback comes out correct because `_v` coincides with the driver's alias — but the bare **array** cannot be
  aliased `_v` at all: `IsNativeArrayProjectionLeaf` demands `alias == GetContainingElementName()`, and
  overriding it anyway is mutation 1 of §5.1, i.e. five rows of silently empty collections. So B must decline
  the array, costing the array win and 11 spec cases.
- **C** fixes tier 1 completely (MEASURED: nullable string, nullable int, and owned array all correct on the
  late-fallback route) but breaks tier 2, because a `_v` alias has no document path to read off whole
  documents. MEASURED: `Select(b => b.Posts.Count)` under default `Native` with a parameterized-regex `Where`
  throws `InvalidOperationException: Document element '_v' is missing but required.` — loud, because `int` is
  non-nullable, but still a working query turned into a throw.
- **D** is the reconciliation: strip when the bare leaf **has** a document path (so the fallback yields whole
  documents, which tier 1 reads correctly), don't strip when it does not (so the fallback stays on the driver
  push-down, whose alias *is* `_v`).

### 6.2 Variant D, measured in full

MEASURED, EF10 spec suite, both axes, re-baselined:

| | |
|---|---:|
| `NativeOnly` Failed→Passed | **80** |
| `NativeOnly` Passed→Failed | **0** |
| default `Native` failures | **1** |
| MQL baselines re-written | 78, across 13 files |
| `#if` lines added/removed under `src/` | **0** |

The single residual is `NorthwindCompiledQueryMongoTest.Multiple_queries`:
`Assert.Contains("Unsupported cross-DbSet query between")` now sees `"Operation is not valid due to the
current state"`. That is an exception-**message** change on an **unsupported** cross-DbSet compiled query — per
the versioning rubric not contract, and a spec-override edit rather than a defect. It should nonetheless be
understood before Task 2 lands; the shape is `AssertNoMultiCollectionQuerySupport`, and it deserves a sentence
in the as-built note.

Functional probe under variant D — every shape correct in every mode, including the ragged/late-fallback
matrix — with **one** exception, which is design §7.4 and nothing else: `Select(b => b.Posts.Count)` under
explicit `DriverLinq` on a **missing** or explicitly-**null** array throws `MongoCommandException` where base
answered `0`.

**Two more spec cases flip usefully under D:** `OrderBy_ThenBy_same_column_different_direction` returns to its
committed EF-253 `"Duplicate element name '_id'"` assertion (variants A/B turn it into
`ArgumentOutOfRangeException`), so D also removes an exception-shape change A/B would have had to re-baseline.

---

## 7. Recommendation on tier 2

**KEEP tier 2 — but only as part of variant D, and only with the owner sighted on one measured change.**

The reasoning, on data:

- **Tier 2 is cheap and it is not where the risk is.** It contributes **6** spec cases (not the design's +22),
  and under variant D it introduces **zero** silent wrong data, **zero** default-`Native` failures, and
  **zero** `NativeOnly` regressions.
- **The one measured cost is exactly the one §7.4 predicted, and it is confined to the explicit escape
  hatch.** `UseQueryMode(DriverLinq)` on `Select(b => b.Posts.Count)` goes from `0` to `MongoCommandException`
  for a missing or explicitly-null stored array. `Native` and `NativeOnly` are correct for every array state.
  Per the rubric this is not a break (`MongoQueryMode` exists at no release tag), and it makes the bare
  spelling agree with the already-documented wrapped spelling
  (`Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_aborts_on_a_missing_array`) rather
  than disagree with it.
- **Dropping tier 2 does not buy safety, it buys nothing.** Variant C (tier 1 only + unconditional strip)
  scores 74/1 — six fewer wins for the same residual. The genuine risk in this slice is entirely in tier 1's
  alias scheme and in the late fallback, and it has to be fixed for tier 1 regardless.
- **If the owner rejects the `DriverLinq` change**, the escape hatch is variant C (drop tier 2, keep the
  *unconditional* strip): **74 wins, 1 residual, no silence**. Note the "78 vs 100" framing in the design is
  not the trade on offer; the real trade is **80 vs 74**.

---

## 8. Does the design's Task 1–5 breakdown survive? Mostly, with one insertion and three rewrites

| Task | Verdict |
|---|---|
| **Task 1** — carrier + read side, inert | **Survives, and its inertness is MEASURED** (byte-identical `NativeOnly` name set). Two required corrections: the `Dictionary<string?, …>` null key does not compile (§3.1); and the §6.2 invariant as specified is a tautology that never fires (§3.2/§5.1) — either redesign it against what the pipeline emits, or drop it and say so, but do **not** ship it as the slice's safety story. Its "mutation-verified in Task 1" step will pass vacuously. |
| **NEW Task 1b** — the late-fallback strip | **Must be inserted, before Task 2.** One conditional in `MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery`'s `Route == Projection` branch, reusing the existing `StripPushedDownSelect`, gated on the bare leaf being path-addressable (§6). Without it Task 2 ships silent wrong data. This is the file the design's §1 table explicitly lists as *not* changed. |
| **Task 2** — tier 1 | Survives. Acceptance criterion must change: **78 MQL re-baselines across 13 files are expected and enumerable**, not "zero diffs". Mutation (a) (revert site A, keep the arm) is worth keeping but note the failure it produces is loud only for non-nullable leaves. Mutation (b) (alias → a literal) is **the** mutation and it is MEASURED to work — it produces five silently empty collections. Add mutation (d): remove the Task-1b strip ⇒ the ragged/parameterized-`Where` tests go red. |
| **Task 3** — tier 2 | Survives. Its `DriverLinq` leg is now MEASURED rather than predicted (§3.3) and must assert the `MongoCommandException`, not paper over it. The `_v` collision guard was never exercised by anything measured here (no fixture has a property stored at `_v`) — keep it, but record it as defence-in-depth, not as a measured need. |
| **Task 4** — EF-362 readiness | Unaffected by anything measured here. |
| **Task 5** — sweeps + docs | Survives, plus: correct §4.2's fallback-route table (it is the driver push-down, not `aggregate([])`); correct §0/§10's 78/100 to 74/+6/80; record site D as the fourth alias site; record `Multiple_queries`; record the incidental widening of bare-`Select`-then-`Count()`/`First()`. |

**Ordering change:** Task 1 → **Task 1b** → Task 2 → Task 3 → Task 4 → Task 5. Task 1b is inert on its own
(nothing sets `IsBareProjection` until Task 2), so it can land in the same "no behaviour change" phase as
Task 1.

**One test-plan gap worth naming loudly.** Every functional test in design §9.3 uses a **constant**-only
`Where`. That is precisely the case where the native factory succeeds and the late fallback never happens.
The new-file test plan must add a **parameterized-`Where`** leg for each bare leaf kind — a captured local in
a `StartsWith`, which is the cheapest reachable native-renderer decline — or the slice's own tests will be
green while the shipped code silently returns nulls.

---

## 9. Reproduction — what I actually ran

```bash
S=<scratchpad>/step3a-task0
git worktree add $S/base 69a65d31 --detach
git worktree add $S/head 69a65d31 --detach

# Both MONGODB_URI and ATLAS_URI unset throughout -> TestContainers boots mongodb/mongodb-atlas-local
# per test process. Every dotnet test redirected to a file, never piped through tail/head.

# --- baseline, verified before any comparison was trusted ---
(cd $S/base && dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10")
env -u MONGODB_URI -u ATLAS_URI dotnet test .../SpecificationTests.csproj -c "Debug EF10" --no-build \
    --logger "trx;LogFileName=base-native.trx"      # -> 4593 / 0 / 17
env -u MONGODB_URI -u ATLAS_URI MONGODB_EF_NATIVE_ONLY=1 dotnet test ... \
    --logger "trx;LogFileName=base-nativeonly.trx"  # -> 2352 / 2241 / 17

# --- the spike, in $S/head/src (7 files, 0 #if delta) ---
#  MongoSelectDefinition            : ProjectionAliasOverrides + writer + TryGetProjectionAlias + IsBareProjection
#                                     (SENTINEL key -- a null Dictionary key throws)
#  MongoQueryExpression             : site A alias derivation + the section 6.2 invariant
#  MongoProjectionBindingExprVisitor: site C alias derivation
#  NativeProjectionBinder           : the bare-body arm (3-step alias derivation, tier 1 + tier 2,
#                                     _v collision guard) + env knobs
#  QMTEV                            : !IsBareProjection on IsPlainProjectedSelect  (section 7.2)
#  NativeGroupByBinder              : decline IsBareProjection                      (section 7.3)
#  MongoShapedQueryCompilingExprVis : the candidate late-fallback strip              (section 6, NEW)
#
# Env knobs: SPIKE_3A=0 (arm off) | SPIKE_TIER2=0 | SPIKE_NARROW=0 | SPIKE_ALIAS_V=1
#            SPIKE_FIXFALLBACK=1 (unconditional strip) | =2 (tier-1-only strip)

# --- spec runs (each ~30-55 s), all compared by failing-test-name SET + message bucket,
#     Assert.Throws classified FIRST ---
#   base-native / base-nativeonly            4593/0/17 ; 2352/2241/17
#   head-native / head-nativeonly (variant A)  85 fail ; 2239 fail  (80 wins hidden behind 76 MQL diffs)
#   control-no  (SPIKE_3A=0)                 name set BYTE-IDENTICAL to base  <- inertness proof
#   tier1only-no (SPIKE_TIER2=0)             72 transitions, 0 outright
#   nonarrow-no / nonarrow-nat (SPIKE_NARROW=0)  109 fail (+24) ; NativeOnly net worse
#   rb-nat / rb-no      (variant A, re-baselined)    7 fail ; 80 wins
#   vopt-nat / vopt-no  (variant B, SPIKE_ALIAS_V=1) 12 fail ; 69 wins
#   fix-nat / fix-no    (variant C, TIER2=0 FIX=1)    1 fail ; 74 wins
#   fix2-nat / fix2-no  (variant D, FIX=2)            1 fail ; 80 wins   <-- RECOMMENDED

env -u MONGODB_URI -u ATLAS_URI EF_TEST_REWRITE_BASELINES=1 dotnet test ...   # 78 baselines, 13 files

# --- functional probe: tests/.../Query/Step3aProbeTests.cs (throwaway, removed with the worktree) ---
#   Ragged 5-row raw-BSON seed: populated(2) / empty / Posts MISSING / Posts explicit BSON null /
#   populated(1) with a DUPLICATE Title (so whole-entity vs projected-value dedup differ).
#   Model: OwnsMany(Posts, HasKey(PostId)) + OwnsOne(Home) + nullable Note/Score + primitive Tags.
#   18 probes x {Native, DriverLinq, NativeOnly} + captured MQL, results appended to a file
#   (SPIKE_PROBE_OUT) rather than asserted, so one run yields the whole disposition matrix.
#   Run at SPIKE_3A=0 (base), variant A, SPIKE_ALIAS_V=1, FIX=1 and FIX=2.

git worktree remove --force $S/base && git worktree remove --force $S/head && git worktree list
```

**Trap compliance.** (a) Every bucket classifies `Assert.Throws`/`ThrowsAny` **before** substring-matching the
message. (b) No routing claim rests on MQL shape — every "goes native" is a `NativeOnly` run that succeeds,
and every MQL quotation here is about the *emitted alias*, which is what §6 is about. (c) Both `MONGODB_URI`
and `ATLAS_URI` unset for every run; the VectorSearch numbers reproducing (20 → 16 with the correct 4+12
split) confirms Atlas really ran. (d) Own scratchpad subdirectory; no other agent's worktree or scratchpad
touched.

**Known limitations.** I did not run EF8 or EF9. I did not run the functional or unit suites in full (only the
probe filter) — variant D's effect on `NativeArrayProjectionTests` / `NativeOwnedCollectionCountTests` /
`NativeSetOpsTests` is **UNVERIFIED** and Task 2/3 must check them. The bare-entity positive control is
inconclusive from my fixture (§5.4). Variant D's conditional (`alias != "_v"`) is a spike expedient; the real
implementation should carry the tier as data on the override rather than sniffing the alias string. I did not
probe a fixture with a real property stored at element `_v`, so the tier-2 collision guard is unexercised.
