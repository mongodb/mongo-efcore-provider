# EF-322 stream 1 (translator breadth) — decomposition spike

*Run 2026-08-07 in a throwaway worktree at `e1fb753d` (created, used, removed; `git worktree list` verified —
the three `.claude/worktrees/agent-*` worktrees belong to other sessions and were not touched). Main tree
finished with only this file added. Inputs: `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`
§3 stream 1; `docs/native-query-status-EF-322.md` §9.0 (method), §9.1 (the figures being re-derived);
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`.*

**Tagging convention, applied strictly.** Every claim is one of:
**MEASURED** (produced by a run this session; the method is §7 below and the numbers are reproducible) ·
**READ** (established by reading source at `e1fb753d`; no execution) ·
**INFERRED** (drawn from MEASURED/READ facts, not itself observed) ·
**UNVERIFIED** (not established — said so explicitly).

**Trap compliance, stated up front.** (a) `Assert.Throws`/`Assert.ThrowsAny` is classified **before** any
substring match on the message. The naive alternative was run and reproduces the documented over-count exactly:
a raw substring match on the instrumentation marker finds **1953** cases where the correct answer is **1517 + 81
= 1598** — every one of the 454 `Assert.Throws` failures quotes an inner
`NativeTranslationNotSupportedException` in its message. (b) No claim here rests on MQL shape as a routing
signal; every number is a `MONGODB_EF_NATIVE_ONLY=1` run. (c) `MONGODB_URI` and `ATLAS_URI` were both unset, so
TestContainers booted its own `mongodb/mongodb-atlas-local`.

---

## Headline — five findings, in order of how much they change stream 1's plan

1. **The 588 substantially reproduces — 580 measured, −1.4%** — and every structural row of §9.1 reproduces
   *exactly* (the 16-row decline-site table, its sole-cause column, and the `BARE 222 / WRAPPED_NEW 156 /
   WRAPPED_INIT 20` split). **15 of the 20 cited per-feature figures reproduce to the case.** The 8-case
   shortfall lives entirely in three classification boundaries, named in §3, none of which changes the shape of
   the decomposition. MEASURED. *(Basis note, added on review: §9.1's per-feature table is **not** disjoint —
   its rows sum to **590**, and §9.1's own footnote reconciles that: "≈590 rather than exactly 588 because 2
   VALUE_OK projection leaves are also joins cases and are counted in both views; the disjoint work-stream table
   above is the authoritative partition." **My per-feature table IS disjoint** — every case is counted once, so
   it sums to the same 580 as the site decomposition in §2. The like-for-like comparison is therefore
   **580 vs 588 (−1.4%)**, disjoint against disjoint. Row-sum against row-sum would be 580 vs 590 (−1.7%), but
   the two columns are not on the same basis, so −1.4% is the figure to use.)*
2. **"One capability" is CONFIRMED for predicate ↔ projection-leaf and REFUTED for sort key.** Predicate and
   projection-leaf converge on the *same method* — `TranslateOperand`, reached from `TranslateComparison` and
   from `TryTranslateValue`, differing only by the `allowNumericWidening` flag — and both bottom out in
   `TryResolveMember`. Sort key does not: `TryTranslateField` returns a `MongoFieldExpression`, and
   `MongoPipelineFactory.RenderSort` **throws** for any other node, because MQL `$sort` accepts only field
   paths. **92 of the 104 sort-key cases therefore need a computed-sort capability (`$set`/`$addFields` +
   `$sort`), which is IR + lowerer + renderer work, not a translator arm** — 92 is a floor, 98 a ceiling; the
   6-case gap is itemized in §4.3. READ + MEASURED. §4.
3. **Stream 1's sole-cause yield is 474, not 588 — and only 400 of that 474 is deliverable without the
   computed-sort slice.** MEASURED sole-cause. **74 of the 474 are slice-B-dependent** (their single decline is
   a sort-position one that no translator arm can reach), spread across **seven** groups — the four §7
   originally caveated (A6, A9, A12, A13) plus **three it did not** (A1, A3, A11) — see §5.1, corrected on
   review. Beyond the 474: **62** need stream 1 **and**
   stream 2 (set ops 32, `Distinct` 26, scalar aggregate 4); **34** need two stream-1 features at once (32) or
   are co-blocked at the `ThenBy` arm (2); **12**
   are additionally blocked by deferred work (joins / composite-PK / entity leaf). There is **no double-count**
   between streams 1 and 2 — the buckets are partitioned by *first*
   decline site — but there is a **dependency**, and the plan does not record it. MEASURED. §5.

   **CORRECTED on the final whole-phase review — the number the checkpoint is held against is ≈508, not 474.**
   This finding originally read "after stream 1 alone, ≈474". That understates it by **34**: §5.2 classifies the
   32 "two stream-1 features at the same site" and the 2 "co-blocked at the `ThenBy` arm" as closing **within
   stream 1**, and then rolled both into the post-stream-2 figure anyway. Both convert once *all* of stream 1
   has shipped — which is precisely when the checkpoint runs. The corrected expectations are
   **≈508 after all of stream 1 with slice B** (474 + 34), **≈400 after all of stream 1 without slice B**
   (the sole-cause tranche only — the slice-B exposure of the 34 was not measured, so no larger
   without-slice-B figure is claimed here; **UNVERIFIED**), and **≈570 after streams 1 and 2 together**
   (unchanged: 474 + 34 + 62). See §5.2.

   **Also corrected there: the 12 are not all deferred.** They are blocked by joins *or* composite-PK *or* the
   **entity leaf** — and the entity leaf is slice 3b, i.e. **stream 3**, which is in the plan. Only the
   joins/composite-PK subset is "never, this release". See §5.2.
4. **`MongoExpressionTranslator.cs` should be split BEFORE slice 1, as a mechanical `partial class` file
   move.** The measurement hands you the split for free: the ~20 features land in exactly three of its regions,
   and ~10 slices will all edit `TranslateOperand`. Extracting *types* would be wrong — the private scope state
   (`_entityType`/`_outerParam`/`_innerPrefix`) is the thing the by-name-retarget guards depend on. §6.
5. **The `Convert` row's label is doing real work and should be kept.** "Cast / `Convert` **operand**" counts a
   comparison whose *operand* is a cast, not just a bare `Convert` node. Classifying by the minimal failing node
   alone puts 39 of those 50 predicate cases under "other operator" instead. Reproducing 50/8/14 required
   adopting the doc's labelling; that was confirmed by re-running with the operand rule added. MEASURED. §3.

---

## 1. Baseline and partition — reproduced exactly

MEASURED, EF10 specification suite, `MONGODB_EF_NATIVE_ONLY=1`, at `e1fb753d` with the §9.0 instrumentation
applied. **Two independent instrumented rounds** (the second adding the operand-aware `Convert` rule), each
preceded by a full `dotnet build`:

| | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| round 1 | 2427 | **2166** | 17 | 4610 |
| round 2 | 2427 | **2166** | 17 | 4610 |

Both match the required baseline `Failed: 2166, Passed: 2427, Skipped: 17` **exactly** — the instrumentation is
behaviour-preserving.

The `Assert.Throws`-first partition of the 2166 reproduces §9.1 to the case:

| | measured | §9.1 |
|---|---:|---:|
| **(a)** genuine coverage gaps | **1598** | 1598 |
| **(b)** fails in every mode | **518** | 518 |
| **(c)** `AssertMql` bookkeeping | **50** | 50 |

…as does the expected-type breakdown inside the 454 `Assert.Throws` failures: `InvalidOperationException` 249,
`ExpressionNotSupportedException` 145, `EqualException` 28, `ArgumentException` 10, `XunitException` 2,
`NotSupportedException` 2 (→ (b)); `TruncationException` 4, `TargetInvocationException` 4,
`MongoCommandException` 4, `FormatException` 4, `InvalidCastException` 2 (→ (a), the "18 execution-time pins").

**The (a) 1598 by first decline site — every row exact, including sole-cause.** Line numbers below are
**pristine** (`e1fb753d`); the instrumented worktree ran 2–8 lines higher and the mapping is in §7.

| first decline site (pristine) | cases | sole-cause |
|---|---:|---:|
| `NativeSlotPopulator.PopulateNativeSlots:107` (`Where`) | 507 | 308 |
| `…TranslateSelect:280` (projection binder) | 398 | 312 |
| `NativeSlotPopulator.PopulateNativeSlots:118` (`OrderBy`) | 134 | 80 |
| `…TranslateDistinct:1318` | 84 | 64 |
| `…BindAggregateOrFallback:1368` | 82 | 82 |
| *(no gate site — hard throw in lowerer/renderer)* | 81 | 0 |
| `…TranslateSelect:197` | 62 | 50 |
| `…TranslateGroupBy:1448` | 58 | 0 |
| `…TryTranslateSetOperation:2448` | 42 | 42 |
| `NativeSlotPopulator.PopulateNativeSlots:215` (catch-all) | 40 | 40 |
| `…TranslateSelect:230` | 36 | 36 |
| `NativeSlotPopulator.PopulateNativeSlots:94` (post-terminal) | 34 | 22 |
| *(`Join`/`GroupJoin`, no site recorded)* | 18 | 0 |
| `…TranslateGroupBy:1441` | 10 | 0 |
| `NativeSlotPopulator.PopulateNativeSlots:171` / `:204`, `…TranslateSelect:245` | 4 each | 4 each |
| **total** | **1598** | |

The 81 with no gate site are exactly §9.1's `$lookup` 54 + non-constant regex 19 + `Not` 8 — they throw from
the lowerer/renderer, not through `ThrowIfNativeOnlyForbidsFallback`.

---

## 2. How the three stream-1 sites decompose

MEASURED. "joins" = the minimal failing node is a `TransparentIdentifier` member or an
`EntityQueryRootExpression`.

| site | joins | composite-PK-shaped | entity leaf | all-leaves-field-OK | **stream 1** | total |
|---|---:|---:|---:|---:|---:|---:|
| `Where` (`:107`) | 127 | 120 | — | — | **260** | 507 |
| `OrderBy` (`:118`) | 30 | — | — | — | **104** | 134 |
| projection (`TranslateSelect:280`) | 118 | — | 46 | 18 | **216** | 398 |
| | | | | | **580** | |

§9.1's own split of the same three sites is 127 / 116 / — / — / **264**; 30 / **104**; 108 / 52 / 18 / **220**.
Two boundary differences account for the whole delta and both are visible:

- **`Where`: 120 vs 116.** 112 of my 120 are in the four reference-`Include` suites (matching §9.1's "112 of the
  116"); the other 8 are `NorthwindAggregateOperatorsQueryMongoTest`, of which §9.1 counted 4 as composite-PK
  and 4 as breadth. My classifier records the *minimal failing node* (`OrderDetail.OrderID` /
  `.ProductID`) and cannot tell "declined by the composite-PK guard" from "an `OrderID` access that failed for
  another reason". **±4, and it is a labelling limit of this spike, not a disagreement about the data.**
- **projection: joins 118 / entity 46 / computed 162 / VALUE_OK 54 vs 108 / 52 / 166 / 54.** VALUE_OK reproduces
  exactly at **54**; the other three net to zero (+10 / −6 / −4), i.e. 10 leaves I classify as joins §9.1 split
  between entity and computed. §9.1 already flags a ±2 fuzz here ("2 VALUE_OK projection leaves are also joins
  cases and are counted in both views").

Selector-body shapes at the projection site: **BARE 222, WRAPPED_NEW 156, WRAPPED_INIT 20** — identical to
§9.1.

---

## 3. The per-feature-group table — the deliverable

MEASURED. `pred` / `sort` / `proj` are the three positions; **sole-cause** is the count whose query recorded
exactly one decline site *and* one feature — i.e. the honest "opening this gate alone turns it green" figure.
**`solB`** (added on review) is the sub-count of `sole-cause` whose single decline is a sort-position one that
no translator arm can reach, so it needs **slice B** (§4.3) as well; `sole−solB` is what the group delivers
without slice B. `cited` is the figure in the merge-plan spec / status-doc §9.1.

**This table is disjoint** — every case appears in exactly one row, so the `total` column sums to the same 580
as §2's site decomposition. **The `cited` column is not**: it sums to **590**, and §9.1's own footnote
reconciles that — *"≈590 rather than exactly 588 because 2 VALUE_OK projection leaves are also joins cases and
are counted in both views; the disjoint work-stream table above is the authoritative partition."* The
like-for-like comparison is 580 against the disjoint **588**.

| # | feature group | pred | sort | proj | **total** | **sole** | solB | cited | reproduced? | what the translator needs |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| 1 | cast / `Convert` operand | 50 | 8 | 14 | **72** | 56 | 6 | 72 | **yes, exact** | `TranslateOperand`'s `Convert` branch: admit a type-changing cast via `$convert`/`$toX` **without** relaxing the narrowing-divergence guard |
| 2 | translator already resolves it as a VALUE (reverted tier 2) | — | — | 54 | **54** | 28 | 0 | 54 | **yes, exact** | *nothing* — the translator already succeeds; the gate needs a synthetic projection alias |
| 3 | `EF.Property` leaf | 38 | 6 | 6 | **50** | 44 | 0 | 48 | +2 | `TryResolveMember` / `TryGetMemberOrEFProperty`: accept a top-level `EF.Property(param, "name")`, not only owned chains |
| 4 | constructed value (tuple / anon / DTO / list) | 16 | 2 | 24 | **42** | 36 | 0 | 50 | **−8** | decompose the construction: per-member comparison in predicate, nested `$project` document in projection |
| 5 | bare constant / query parameter | 30 | 10 | — | **40** | 40 | 10 | 40 | **yes, exact** | no new node kind — admit a whole-node `Constant`/`QueryParameterExpression` at each gate |
| 6 | `Nullable.HasValue` / `.Value` | 10 | — | 28 | **38** | 36 | 0 | 38 | **yes, exact** | `TryResolveMember`: peel `Nullable<>.Value`; map `.HasValue` to `$ne: null` |
| 7 | `Contains` over a client collection | 18 | 18 | — | **36** | 36 | 18 | 36 | **yes, exact** | `TryMatchContainsMethod`: accept a client-collection source into the existing `MongoInExpression` |
| 8 | `Coalesce` (`??`) | 2 | 22 | 8 | **32** | 18 | 8 | 32 | **yes, exact** | **new node kind** — `$ifNull` in `TranslateOperand`, plus renderer + `IsQueryDialectRenderable` + negator |
| 9 | `Add` (string concat / arithmetic) | 10 | 4 | 18 | **32** | 16 | 0 | 32 | **yes, exact** | `$add` already exists for numerics; string operands need `$concat` |
| 10 | entity equality (`o == someOrder`) | 30 | — | — | **30** | 22 | 0 | 34 | −4 | decompose to a primary-key comparison before `TranslateComparison`; no new node kind |
| 11 | other client / BCL method call | 8 | — | 22 | **30** | 30 | 0 | 30 | **yes, exact** | per-method recognizers (`ToString`→`$toString`, `Abs`→`$abs`, `IndexOf`→`$indexOfCP`); `ToList`/`ToArray`/`AsEnumerable` are strippable wrappers |
| 12 | `Conditional` (`?:`) | 2 | 10 | 14 | **26** | 26 | 10 | 26 | **yes, exact** | **new node kind** — `$cond`, plus renderer + `IsQueryDialectRenderable` + negator |
| 13 | other arithmetic / comparison operator | 16 | 4 | 2 | **22** | 22 | 4 | 18 | +4 | widen `MapArithmeticOperator` / `MapComparisonOperator` coverage |
| 14 | unary `Negate` | — | — | 18 | **18** | 8 | 0 | 18 | **yes, exact** | `$multiply: [-1, x]` (or `$subtract` from 0) in `TranslateOperand` |
| 15 | `Not` over a non-native operand | — | 18 | — | **18** | 18 | 18 | 18 | **yes, exact** | `MongoExpressionNegator` over a **computed** operand — and slice B, since all 18 are sort |
| 16 | `Equals(...)` method | 16 | — | — | **16** | 16 | 0 | 16 | **yes, exact** | map `a.Equals(b)` onto `Equal` in `TranslateComparison`; no new node kind |
| 17 | `GetType` / type test | 8 | — | — | **8** | 8 | 0 | 8 | **yes, exact** | reuse the discriminator predicate `TranslateOfType` already builds |
| 18 | array literal | 2 | — | 4 | **6** | 6 | 0 | 10 | −4 | `NewArrayInit` → a `$literal` array (already partly works as an `$in` right-hand side) |
| 19 | other member access | — | 2 | 4 | **6** | 4 | 0 | 6 | **yes, exact** | `TryResolveMember` for the residual member shapes |
| 20 | `EF.Functions.Like` | 4 | — | — | **4** | 4 | 0 | 4 | **yes, exact** | route to the existing `MongoRegexExpression` (SQL-pattern → regex translation) |
| | **total** | **260** | **104** | **216** | **580** | **474** | **74** | 590 (≡588 disjoint) | −8 | |

**The "what the translator needs" column is INFERRED** — it names the decline site (READ) and the *shape* of the
fix, not a committed design. Each slice still owns its own design decision; the column exists so the
implementation plan can size a slice without re-reading the translator.

**Fifteen of twenty rows reproduce exactly, and every per-position cell of those fifteen matches too**
(e.g. `Convert` 50/8/14, `Coalesce` 2/22/8, `Add` 10/4/18, `Conditional` 2/10/14, `Nullable` 10/—/28).
The five that differ move ≤8 cases and pair off: entity equality −4 against other-operator +4 (a
classification-order boundary on `Equal` where one side is an entity type), constructed value −8 and array
literal −4 against `EF.Property` +2 (projection-leaf boundaries). **None of them changes which slice a case
belongs to at the granularity the implementation plan needs.**

**On the `Convert` row specifically (finding 5).** Round 1 classified strictly by the minimal failing node and
produced `Convert` = 11 / other-operator = 53 in predicate position. Round 2 added one rule — *a comparison
whose operand is a `Convert`/`ConvertChecked`/`TypeAs` classifies as `Convert`* — and produced **exactly
50 / 8 / 14**. This is the doc's own wording ("cast **operand**") and it should be preserved in the slice
definition: the fix site is `TranslateOperand`'s `Convert` branch either way, so the operand-level label is the
one that names the work.

**Where each group's cases live (MEASURED, top classes)** — for slice sizing:

| group | concentration |
|---|---|
| casts | `NorthwindWhereQuery` 30, `NorthwindMiscellaneous` 20, `BuiltInDataTypes` 8 |
| VALUE_OK tier 2 | `NorthwindSetOperations` 22, `NorthwindSelect` 20, `NorthwindMiscellaneous` 8 |
| `EF.Property` | `NorthwindMiscellaneous` 16, `NorthwindWhere` 16, `NorthwindAggregateOperators` 8 |
| constructed value | `NorthwindSelect` 18, `NorthwindWhere` 10, `NorthwindMiscellaneous` 10 |
| bare constant/parameter | `NorthwindWhere` 22, `NorthwindMiscellaneous` 16 |
| `Nullable.Value` | `NorthwindSelect` 20, `NorthwindMiscellaneous` 18 |
| client-collection `Contains` | `NorthwindAggregateOperators` 12, then 4 each across the four `*Include*` suites |
| `Add` / concat | `NorthwindMiscellaneous` 22, `NorthwindSelect` 6 |
| `??` | `NorthwindMiscellaneous` 30 |
| entity equality | `NorthwindMiscellaneous` 30 (all of them) |
| `?:` | `NorthwindSelect` 14, then 2 each across the `*Include*` suites |
| `Not` | 4 each across the four `*Include*` suites, `NorthwindMiscellaneous` 2 |
| unary `Negate` | `NorthwindSetOperations` 10, `NorthwindSelect` 8 |
| `EF.Functions.Like` | `NorthwindDbFunctionsQuery` 4 (all of them) |

---

## 4. Is it one capability? — CONFIRMED for two positions, REFUTED for the third

The spec's slice split rests on "the same ~20 features recur in predicate, sort-key and projection-leaf
position … `MongoExpressionTranslator` is the one place that has to learn them." Established by **reading**, per
the brief, for four groups.

### 4.1 The call graph (READ, `e1fb753d`)

```
predicate         TryTranslate       → TranslateNode → TranslateComparison
                                                     → TranslateOperand(allowNumericWidening: false)
                                                                         → TryResolveMember
sort key          TryTranslateField  → Unwrap        → TryResolveMember                    ← and nothing else
projection leaf   NativeProjectionBinder.TryTranslateLeaf
                     ├─ field leaf     → TryTranslateField → TryResolveMember
                     └─ computed leaf  → TryTranslateValue → TranslateOperand(allowNumericWidening: true)
                                                                         → TryResolveMember
```

`TranslateComparison:511/:515` and `TryTranslateValue:143` are the two callers of `TranslateOperand`, and the
**only** difference between them is the `allowNumericWidening` flag. `TryResolveMember` is reached from all
three positions.

### 4.2 Group-by-group

**Group 1 — casts (72: pred 50 / sort 8 / proj 14). SHARED ENTRY POINT.** Both the predicate and the
projection-computed path reach `TranslateOperand`'s `Convert` branch
(`MongoExpressionTranslator.cs:594–601`), which rejects a type-changing cast unless
`allowNumericWidening && IsWideningNumericConvert(...)`. **Literally the same lines.** One arm serves 64 of the
72. The 8 sort cases are computed booleans under a cast (`Convert`-over-`GreaterThan`), which sort position
cannot express at all (below).

**Group 2 — `EF.Property` leaf (50: pred 38 / sort 6 / proj 6). SHARED ENTRY POINT, all three positions.**
`TryResolveMember`'s fast path takes only `MemberExpression { Expression: ParameterExpression }`; every other
shape — including an `EF.Property(...)` call — is delegated to `TryResolveOwnedFieldPath`, which requires a
valid *owned* chain and declines a plain top-level `EF.Property<T>(o, "Scalar")`. The fix is one resolution site
and its result is a `MongoFieldExpression`, so it lands in **all three** positions including sort. This is the
cleanest instance of the spec's claim.

**Group 3 — `Coalesce` (32: pred 2 / sort 22 / proj 8). SPLIT.** Neither `TranslateNode` nor `TranslateOperand`
has a `Coalesce` arm, so predicate and projection share one fix (an `$ifNull` arm in `TranslateOperand`) —
10 cases. **But 22 of the 32 — the majority of the group — are in sort position**, and no translator arm
reaches them: see 4.3.

**Group 4 — `Nullable.HasValue` / `.Value` (38: pred 10 / proj 28). SHARED ENTRY POINT.** `x.A.Value` is a
`MemberExpression` whose receiver is itself a `MemberExpression`, so it misses `TryResolveMember`'s fast path
and is handed to `TryResolveOwnedFieldPath`, which walks hops requiring embedded navigations and declines. One
peel in `TryResolveMember` serves both positions and would serve sort too.

### 4.3 Why sort key is a different capability (READ, and it is structural)

`TryTranslateField` returns `MongoFieldExpression`. `MongoOrdering.KeySelector` is typed `MongoExpression`, so
the IR is general — but `MongoPipelineFactory.RenderSort` (`:301–315`) **hard-throws**:

```csharp
if (ordering.KeySelector is not MongoFieldExpression field)
    throw new NativeTranslationNotSupportedException(
        $"$sort key selector must be a MongoFieldExpression; got '{...}'. "
        + "Non-field sort keys should have been rejected by the translator.");
```

That is not a gap in the translator; it is MQL. `$sort` accepts a document of *field paths* only. A computed
sort key requires `$set`/`$addFields` of a synthetic field, `$sort` on it, and (for a whole-entity result) a
`$unset` — a new stage plus lowerer and renderer changes.

**Sizing (MEASURED classification, INFERRED field-shapedness).** Of the 104 sort-key breadth cases:

| | cases | needs |
|---|---:|---|
| `??` 22, `Not` 18, `Contains` 18, `?:` 10, bare const 10, `Convert`-over-comparison 4, `Add` 4, other operator 4, constructed value 2 | **92** | **computed-sort capability**, in addition to any translator arm |
| `EF.Property` 6 | 6 | translator only — **certain**: the fix is in `TryResolveMember` and its result *is* a `MongoFieldExpression` |
| `Convert`-node 4, other member 2 | 6 | **ambiguous** — the minimal failing node is a cast / a member, but whether the enclosing sort key resolves to a field ref was not established |

**Justification for "≥92" (finding 3 on review).** The 92-row is exhaustive of the cases *proven* to need a
non-field sort key, so 92 is a **floor**. The ceiling is **98**: the 6 ambiguous cases could go either way, and
resolving them needs a mutation of `RenderSort` that this spike did not run. Only the `EF.Property` 6 are
certainly translator-only. So: **92 ≤ N ≤ 98**, best estimate 92. UNVERIFIED above 92.

**Verdict.** Stream 1 is **not one capability, it is two**:

- **Capability A — expression breadth in `TranslateOperand` / `TryResolveMember`.** ~476–488 cases (predicate
  260 + projection 216 + the ≤12 field-shaped sort cases). The spec's "one place has to learn them" is **exactly
  right for this part**, and the predicate↔projection identity is stronger than the spec claims: it is the same
  *method*, not two similar ones.
- **Capability B — computed sort keys.** 92 cases (floor; ceiling 98). Not a `MongoExpressionTranslator` change
  at all. It was not named anywhere in the merge plan when this spike ran. *(Update: the owner has since ruled
  it into the plan on the strength of this section — see `17c5525f`.)*

This does not invalidate merging the 368 and 220 buckets — the merge is correct for the 480-odd cases that
matter most — but **a stream-1 implementation plan written as "20 translator slices" will silently under-deliver
by ~92 cases unless capability B is scheduled as its own slice.**

---

## 5. Stream 1's realistic yield is 474 sole-cause / ≈508 all-in, not 588 — and 400 without slice B

*(Heading corrected on the final whole-phase review. It read "…is 474, not 588 — and 400 without slice B",
which conflated the **sole-cause** figure with the **post-stream-1** figure. 474 is sole-cause; **≈508** is what
all of stream 1 delivers, because §5.2's 34 also close within stream 1. §5.2 states both.)*

### 5.1 The slice-B split of the 474 (added on review — finding 2)

MEASURED, by re-querying the retained round-2 sweep. Applying the rule *"a sole-cause case whose single decline
is in sort position and is not `EF.Property` needs slice B"*:

| | cases |
|---|---:|
| stream-1 sole-cause | **474** |
| …of which **slice-B-dependent** | **74** |
| **…deliverable without slice B** | **400** |

Which groups carry it — **seven**: the four §7 originally caveated (A6, A9, A12, A13) **plus three it did not**
(A1, A3, A11). 4 + 3 = 7, and 7 + the 13 zero rows = the 20 groups of §3.

| group | sole-cause | slice-B-dependent | without slice B |
|---|---:|---:|---:|
| A13 `Not` | 18 | **18** | **0** |
| A6 client-collection `Contains` | 36 | 18 | 18 |
| A3 bare constant / query parameter | 40 | **10** | 30 |
| A9 `?:` | 26 | 10 | 16 |
| A12 `??` | 18 | 8 | 10 |
| A1 casts | 56 | **6** | 50 |
| A11 other arithmetic / comparison | 22 | 4 | 18 |
| all other groups (13) | 258 | 0 | 258 |
| **total** | **474** | **74** | **400** |

**This corrects two claims made in the first version of this document.**

1. §7's "**Slices A1–A5 are 204 sole-cause … none of which needs slice B**" was **wrong**. Measured, A1 carries
   **6** slice-B-dependent cases and A3 carries **10**, so A1–A5 is **204 sole-cause of which 16 need slice B →
   188 without it**. A1's exposure was not visible from §4.2's prose, which described A1's 8 sort cases as split
   4 field-shaped / 4 not; the sole-cause split is different from the total split — of A1's 8 sort cases,
   4 (`Convert`-over-comparison) are all sole-cause and 2 of the other 4 (`Convert`-node) are too, giving 6.
2. A3 was never caveated at all, yet it is 100% sole-cause, so **every one of its 10 sort cases is a direct
   loss** if slice B does not ship.

**Residual uncertainty:** 2 of the 74 are the ambiguous `Convert`-node sort cases of §4.3, which might turn out
field-shaped. The honest range is **72–74 slice-B-dependent / 400–402 without slice B**. UNVERIFIED at that
precision; 74/400 is the conservative reading.

**Why this matters for sequencing, not just accuracy.** The "204 with zero slice-B risk" framing is what made
A1–A5 look safe to run *before* slice B. It is not: running A1–A5 first still leaves 16 of their cases dark
until slice B lands. A1, A2, A4 and A5 remain genuinely slice-B-independent at 50 / 44 / 28 / 36 = **158**, and
that — not 204 — is the true "safe to start now" tranche.

### 5.2 Beyond the sole-cause set

MEASURED. Of the 582 cases whose first decline site is one of the three stream-1 sites and which carry at least
one stream-1 feature:

| | cases | closes when |
|---|---:|---|
| exactly one decline site, one feature | **474** | that feature's slice ships |
| co-blocked at `TryTranslateSetOperation:2448` | 32 | **stream 1 + stream 2** (set ops) |
| co-blocked at `TranslateDistinct:1318` | 26 | **stream 1 + stream 2** (`Distinct`) |
| two stream-1 features at the same site | 32 | **within stream 1** — when both slices ship |
| co-blocked at the `ThenBy` arm (`:128`) only | 2 | **within stream 1** |
| co-blocked at `BindAggregateOrFallback:1368` | 4 | **stream 1 + stream 2** (scalar aggregate) |
| additionally blocked by joins / composite-PK / **entity leaf** | **12** | joins/composite-PK subset: **never, this release**; entity-leaf subset: **stream 3** (see below) |

**The ±2 against §2 and §3, added on the final whole-phase review.** This section's population is **582**; §2's
site decomposition and §3's disjoint per-feature table both total **580**. The document reconciles 580-vs-588
and 580-vs-590 at length and never mentions this one, so it is recorded here rather than left to be
re-discovered. The two populations are *not* defined identically — §2/§3 count the cases the classifier assigned
to the **stream-1** column of the site decomposition, whereas §5.2 counts cases whose *first* decline site is one
of the three stream-1 sites **and** which carry at least one stream-1 feature, which admits cases the site
decomposition assigned to the joins / composite-PK / entity-leaf columns (the 12 row is exactly such a set).
**The cause of the 2-case difference was not established** — the ±2 fuzz §2 already documents (2 VALUE_OK
projection leaves that are also joins cases, counted in both views) is a candidate of the right size, but it was
not confirmed, and no other candidate was tested. **UNVERIFIED.** It is ≤0.4% and does not move any figure this
document plans against; do not treat it as reconciled.

**There is no double-count between streams 1 and 2** — the two buckets are partitioned by *first* decline site,
so the 62 cases above are counted once, in stream 1. But there is an undocumented **dependency**: 62 of stream
1's cases convert only once stream 2 has also landed.

**The 12 are not all deferred — corrected on the final whole-phase review.** This row originally read
"**never, this release** (all deferred)". That is wrong for one of its three blockers: the **entity leaf** is
slice 3b, which is **stream 3** of the merge plan (52 cases) and is explicitly scheduled. Any of the 12 blocked
*only* by the entity leaf therefore converts when stream 3 lands. This spike did not break the 12 down by
blocker, so **the corrected figure is not derivable from this report and none is invented here**: only the
joins/composite-PK-blocked subset is "never, this release", the entity-leaf-blocked subset converts with
stream 3, and **the split is UNVERIFIED**. The consequence for the plan is one-directional and safe —
**≈570 and the 3331 projection built on it are conservative by exactly that subset**, never optimistic.

The plan's §7 re-measurement checkpoint should therefore expect:

- **after all of stream 1, with slice B: ≈508** — the 474 sole-cause **plus** the 34 that §5.2 classifies as
  closing within stream 1 (32 needing a second stream-1 feature, 2 at the `ThenBy` arm). *(Corrected on the
  final whole-phase review: this bullet read "**after stream 1 alone, with slice B: ≈474**", which is the
  sole-cause figure, not the post-stream-1 figure — the 34 were classified as closing within stream 1 in the
  table above and then rolled into the post-stream-2 figure anyway. Understated by 34.)* ≈508 is still an
  **upper bound** — sole-cause means nothing else declined at *population* time; the lowerer or renderer can
  still decline, as the 81 no-gate-site cases show;
- **after all of stream 1, without slice B: ≈400** (§5.1) — the sole-cause tranche only. Whether any of the 34
  is itself slice-B-dependent was **not measured**, so no larger without-slice-B figure is claimed.
  **UNVERIFIED**;
- **after streams 1 + 2 together: up to ≈570** from stream 1's bucket (474 + 34 + 62) — unchanged by the
  correction above, which only moves cases between the two earlier lines.

If the checkpoint measures ~508 after stream 1, **that is the expected result, not a shortfall** — reading it as
one and pulling joins back in would be an error. Judging it against 588 would be the same error, one size
larger. INFERRED from the MEASURED table above.

---

## 6. `MongoExpressionTranslator.cs` — split it before slice 1, as a partial class

READ. The file is 1478 lines / 36 members. Where the ~20 features land:

| region | members | lines | features that land here |
|---|---|---:|---|
| **entry + core dispatch** | `TryTranslate`, `TryTranslateField`, `TryTranslateValue`, `Unwrap`, `TranslateNode`, `TranslateComparison`, `TranslateOperand`, `TranslateValue`, operator maps, numeric helpers | ~59–768 (≈700) | casts, `??`, `?:`, `Add`, `Negate`, other operators, bare const/param, array literal, entity equality, constructed value |
| **member resolution** | `TryResolveMember`, `TryResolveOwnedFieldPath`, `TryGetMemberOrEFProperty`, `TryResolveOwnedCollectionPath` | ~769–938 + 1234–1289 (≈230) | `EF.Property`, `Nullable.Value`, other member access, composite-PK |
| **method-call recognizers** | `TryMatchQuantifierMethod`, `TryMatchCountExpression`, `IsCanonicalCountWithPredicate`, `TryMatchContainsMethod`, `TryMatchRegexMethod`, `TranslateInValues`, `UnwrapAsQueryable`, `ReferencesEnclosingScope`, `FreeParameterVisitor` | ~939–1233 + 1322–1473 (≈450) | client-collection `Contains`, `Equals()`, `GetType`/type test, `EF.Functions.Like`, other BCL method calls |

**Recommendation: split it into three `partial class` files *before* slice 1** —
`MongoExpressionTranslator.cs` (entry + core), `MongoExpressionTranslator.Members.cs`,
`MongoExpressionTranslator.MethodCalls.cs`. Reasons, in order:

1. **~10 of the 20 slices all edit `TranslateOperand`.** They will be developed in sequence on one branch, but
   the file is also the single hottest merge point with anything else in flight.
2. **The split is a pure file move** — no signature, no visibility, no semantics change; a `partial` declaration
   and three `#region`-sized cuts. It can be verified by `git diff --stat` plus a green build, with none of the
   review cost of a real refactor.
3. **Do it before, not during.** Doing it mid-stream means one slice's diff mixes a 1478-line move with a
   behaviour change, which is exactly the review shape this branch has been corrected for.

**Do NOT extract types.** The three regions all read the private scope state `_entityType` / `_outerParam` /
`_outerEntityType` / `_innerPrefix`. Extracting a `MemberResolver` or `MethodCallRecognizer` type forces that
state through parameters, and the by-name-retarget hazard the codebase documents at length
(`TryResolveOwnedCollectionPath`'s "INHERITED INVARIANT" remark, `ReferencesEnclosingScope`,
`NativeSelectManyBinder.ReferencesParameter`) is precisely a hazard about which scope a member resolves
against. `partial` keeps the state private with one owner. **No other refactoring is proposed** — in particular
the `IsQueryDialectRenderable` ↔ `RenderNode` ↔ `MongoExpressionNegator` three-way contract stays where it is;
it is a per-slice obligation (§7 below), not a structural problem.

---

## 7. Proposed slice split

> **⚠ NUMBERING COLLISION — read this before citing a number from either table (added on the final
> whole-phase review).** §3's feature groups are numbered **1–20 by descending group total**; this section's
> slices are lettered **A1–A20 by yield-per-unit-of-work** (roughly, but not exactly, by descending
> sole-cause). **The two orderings are different, and they collide in the worst possible way: the same small
> integer means different features in each.** Examples: **§3 group 3 is `EF.Property`, but A3 is bare constant
> / query parameter**; **§3 group 5 is bare constant, but A5 is `Nullable.HasValue`/`.Value`**; §3 group 9 is
> `Add`, A9 is `?:`. Only **group 1 → A1** is a fixed point; every other number moves.
> **Stream 1's implementation plan is written one slice per group from these two tables**, so a bare "slice 5"
> or "group 9" is ambiguous and has already produced one wrong list in this document (see the correction under
> slice B). **Always write the `A`-prefix when you mean a slice, and say "§3 group N" when you mean a group.**
> The full mapping, §3 group → slice: 1→A1, 2→A4, 3→A2, 4→A7, 5→A3, 6→A5, 7→A6, 8→A12, 9→A14, 10→A10, 11→A8,
> 12→A9, 13→A11, 14→A17, 15→A13, 16→A15, 17→A16, 18→A18, 19→A19, 20→A20.

Sized by **sole-cause** (the yield if that slice ships alone) with the group **total** as the upper bound.
Ordered by yield-per-unit-of-work, with the two hard prerequisites first.

### Slice 0 — file split (0 cases)
The `partial class` move of §6. Pure mechanical; gate is a green three-EF-version build and a zero-delta spec
sweep.

### Slice B — computed sort keys (92 cases enabled, of which **74 are stream-1 sole-cause**; 0 delivered alone)
`$set`/`$addFields` + `$sort` + `$unset`, a new `MongoAddFieldsStage`, `RenderSort` accepting a non-field
`KeySelector`, and `NativeSlotPopulator`'s `OrderBy`/`ThenBy` arms calling `TryTranslate`/`TryTranslateValue`
instead of only `TryTranslateField`. **Delivers nothing on its own** — it is the multiplier that lets the sort
column of slices **A1, A3, A6, A9, A11, A12 and A13** count. Schedule it early or accept that **~92 of the 104
sort-key cases** (inside the measured 580) will not convert.
UNVERIFIED: whether a synthetic sort field survives the whole-entity DOM/streaming shapers untouched; that is
the spike this slice must open with.

*(Both figures corrected on the final whole-phase review, and they are the residual of the miscount that
commit `2431bbf0` was raised to fix.* **(i)** *The slice list read "slices 5, 8, 9, 12, 13" — **five**, where
§5.1 measured **seven**, and ambiguous between §3's group numbering and §7's A-numbering (see the collision
warning at the head of §7). It is wrong under either reading: under §3 numbering it names bare-const, `??`,
`Add`, `?:` and other-operator, omitting casts, `Contains` and `Not` and wrongly including `Add`; under
A-numbering it names `Nullable`, other-BCL, `?:`, `??` and `Not`, two of which have no sort cases at all. The
seven above are §5.1's measured slice-B-dependent* **sole-cause** *carriers. At the level of* total *sort cases
rather than sole-cause, §4.3 also puts 4 `Add` (A14) and 2 constructed-value (A7) cases inside the 92 — neither
carries any sole-cause exposure, which is why neither is in the seven.* **(ii)** *"~92 of the 588" mixed two
bases: 92 is a subset of the **104 sort-key cases**, and 588 is the superseded cited total. The measured total
is 580.)*

### Capability-A slices, by measured yield

`sole` is the sole-cause yield **with** slice B; `−B` is what the slice delivers **without** it (§5.1).

| slice | feature | total | **sole** | **−B** | notes |
|---|---|---:|---:|---:|---|
| A1 | cast / `Convert` operand | 72 | **56** | 50 | `TranslateOperand`'s `Convert` branch. Highest single yield. Narrowing casts are the documented divergence risk — do not simply relax the guard. **6 of the 56 need slice B** (corrected on review; 2 of those 6 are the ambiguous cases of §4.3). |
| A2 | `EF.Property` leaf | 50 | **44** | 44 | `TryResolveMember` / `TryGetMemberOrEFProperty`. Lowest-risk slice on the board — pure resolution, no new node kind, lands in all three positions **including sort**. **Best first slice after slice 0.** |
| A3 | bare constant / query parameter | 40 | **40** | 30 | 100% sole-cause. **10 of the 40 are sort position and need slice B** (corrected on review — this slice was previously uncaveated). |
| A4 | VALUE_OK / reverted tier 2 (projection) | 54 | **28** | 28 | **Has a recorded prerequisite** — see the step-3a note in `Query/AGENTS.md`: tier 2 was built, measured and reverted because the late-fallback path inherits the driver's bare `$size`. Do not re-attempt without that fixed. |
| A5 | `Nullable.HasValue` / `.Value` | 38 | **36** | 36 | `TryResolveMember` peel. Pairs naturally with A2. |
| A6 | client-collection `Contains` | 36 | **36** | 18 | 18 predicate (existing `MongoInExpression`), 18 sort (**needs slice B**). |
| A7 | constructed value (tuple / anon / DTO / list) | 42 | **36** | 36 | Predicate half is tuple comparison; projection half is a nested construction. Check whether these are really one slice before committing. |
| A8 | other client / BCL method call | 30 | **30** | 30 | Long tail: `ToString`, `Abs`, `IndexOf`, `ToList`/`ToArray`/`AsEnumerable`, `FirstOrDefault`, a client method. Split per method if it does not fit one slice. |
| A9 | `Conditional` (`?:`) | 26 | **26** | 16 | New `$cond` node ⇒ renderer + `IsQueryDialectRenderable` + negator, all three. 10 of 26 need slice B. |
| A10 | entity equality | 30 | **22** | 22 | Decompose to key comparison; all 30 in `NorthwindMiscellaneous`. No new node kind. |
| A11 | other arithmetic / comparison operator | 22 | **22** | 18 | 4 need slice B. |
| A12 | `Coalesce` (`??`) | 32 | **18** | 10 | New `$ifNull` node. **22 of the 32 cases are sort position** — without slice B this slice yields 10. |
| A13 | `Not` over a non-native operand | 18 | **18** | **0** | **All 18 are sort position** — yields **zero** without slice B. |
| A14 | `Add` (string concat / arithmetic) | 32 | **16** | 16 | `$concat` for the string case. |
| A15 | `Equals(...)` method | 16 | **16** | 16 | Map to `Equal`; no new node kind. Cheap. |
| A16 | `GetType` / type test | 8 | **8** | 8 | Reuse `TranslateOfType`'s discriminator machinery. |
| A17 | unary `Negate` | 18 | **8** | 8 | Projection only. |
| A18 | array literal | 6 | **6** | 6 | |
| A19 | other member access | 6 | **4** | 4 | |
| A20 | `EF.Functions.Like` | 4 | **4** | 4 | Reuse `MongoRegexExpression`. All 4 in one class. |
| | **total** | **580** | **474** | **400** | |

**Totals: 580 upper bound, 474 sole-cause with slice B, 400 without.** Slices A1–A5 are **204 sole-cause**, but
**16 of those need slice B**, so they deliver **188** on their own — and the genuinely slice-B-independent
tranche is **A1, A2, A4, A5 = 158**. *(Corrected on review: the first version of this document claimed
"A1–A5 … 204, none of which needs slice B", which is false — see §5.1.)*

**Per-slice obligation, for every slice that introduces a new `MongoExpression` node kind** (A9, A12, A14, and
possibly A1): `MongoQueryLanguageRenderer.IsQueryDialectRenderable`, `MongoQueryLanguageRenderer.RenderNode` /
`MongoAggregationExpressionRenderer.Render`+`CanRender`, and `MongoExpressionNegator.TryNegate` must change
together. `Query/AGENTS.md` records why: a node kind the negator produces but the renderer cannot express is a
**hard server error inside `$elemMatch`**, not a decline. READ.

---

## 8. How to reproduce

```bash
SCRATCH=<scratchpad>
git worktree add $SCRATCH/wt e1fb753d
```

Instrumentation applied in the worktree (5 files, all `internal`, all reverted with the worktree):

1. **`Expressions/MongoSelectDefinition.cs`** — `MarkNotNativelyRepresentable` gains
   `[CallerMemberName]`/`[CallerLineNumber]`/`[CallerFilePath]`, recording the **first** site and the
   de-duplicated **list** of all sites; plus `InstrRecord(string)` / `InstrReport`.
2. **`NativeTranslation/InstrDeclineClassifier.cs`** (new) — the minimal-failing-subtree search (re-invoke a
   **fresh** translator on each child, descend into the first failing one, **never** descend into a bare
   `ParameterExpression`, depth cap 12) and the feature classifier. `Fails(e)` = all three of `TryTranslate`,
   `TryTranslateField`, `TryTranslateValue` returning false.
3. **`NativeTranslation/NativeSlotPopulator.cs`** — the `Where` / `OrderBy` / `ThenBy` decline arms record
   `WHERE|<feature>|<nodeType>` / `SORT|…`.
4. **`NativeTranslation/NativeProjectionBinder.cs`** — `TryPopulateNativeProjection` renamed to
   `…Core` and wrapped; on `false` it records `PROJ|<BARE|NEW|INIT>|<ENTITY|VALUE_OK|NO_XLATE|ALL_FIELD_OK>|<feature>|<nodeType>`,
   classifying the **first leaf the existing translator cannot already resolve as a field**.
5. **`Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`** — `ThrowIfNativeOnlyForbidsFallback` takes an
   optional `MongoQueryExpression` and appends `Select.InstrReport` to the message, so the `.trx` carries
   per-test attribution.

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/... \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=s1.trx"
```

Parse: classify `AssertBaseline`-in-stack → (c); `Assert.Throws`/`Assert.ThrowsAny` by
`Expected: typeof(X)` → (a) if X is an execution-time type, else (b); `Assert.Contains` → (b); rest → (a).
Then regex `\|\|INSTR first=(\S*) sites=(\S*) detail=(.*?)\|\|` on the (a) messages.

**Instrumented-to-pristine line-number map** (the instrumentation adds 3 lines per wrapped arm):
`:109→:107`, `:123→:118`, `:136→:128`, `:180→:171`, `:213→:204`, `:224→:215`. Every other site is unshifted.
**Re-derive rather than trusting these after any edit to those files.**

---

## 9. What is UNVERIFIED

- Whether a synthetic `$set` sort field survives the whole-entity DOM and streaming shapers untouched (slice B's
  opening spike must answer it).
- The field-shapedness split of the 104 sort cases (92 certain / 6 certain-field / 6 ambiguous) is INFERRED from
  the feature classification plus `RenderSort`'s contract; it was not measured by mutating `RenderSort`. The
  same ambiguity puts §5.1's slice-B-dependent sole-cause figure in the range **72–74** (74 quoted, the
  conservative end).
- Whether the `EF.Property` fix in `TryResolveMember` really lands in all three positions was established by
  READING the call graph, not by shipping it.
- Whether the 4 ambiguous `NorthwindAggregateOperatorsQueryMongoTest` cases are composite-PK or breadth (the ±4
  in §2). Resolvable by instrumenting `TryResolveMember`'s composite-PK guard specifically.
- Nothing here re-derived §9.1's per-test-class table (§7.1), the `Native`-axis MQL baselines, or the (b)/(c)
  partitions beyond their totals.
