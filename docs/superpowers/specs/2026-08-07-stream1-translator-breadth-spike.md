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
   the decomposition. MEASURED.
2. **"One capability" is CONFIRMED for predicate ↔ projection-leaf and REFUTED for sort key.** Predicate and
   projection-leaf converge on the *same method* — `TranslateOperand`, reached from `TranslateComparison` and
   from `TryTranslateValue`, differing only by the `allowNumericWidening` flag — and both bottom out in
   `TryResolveMember`. Sort key does not: `TryTranslateField` returns a `MongoFieldExpression`, and
   `MongoPipelineFactory.RenderSort` **throws** for any other node, because MQL `$sort` accepts only field
   paths. **≥92 of the 104 sort-key cases therefore need a computed-sort capability (`$set`/`$addFields` +
   `$sort`), which is IR + lowerer + renderer work, not a translator arm.** READ + MEASURED. §4.
3. **Stream 1's realistic yield is 474, not 588** — that is the MEASURED sole-cause count. A further **62** need
   stream 1 **and** stream 2 (set ops 32, `Distinct` 26, scalar aggregate 4); **34** need two stream-1 features
   at once; **12** are additionally blocked by deferred work (joins / composite-PK / entity leaf) and cannot
   convert this release. There is **no double-count** between streams 1 and 2 — the buckets are partitioned by
   *first* decline site — but there is a **dependency**, and the plan does not record it. MEASURED. §5.
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
`cited` is the figure in the merge-plan spec / status-doc §9.1.

| # | feature group | pred | sort | proj | **total** | **sole-cause** | cited | reproduced? |
|---|---|---:|---:|---:|---:|---:|---:|---|
| 1 | cast / `Convert` operand | 50 | 8 | 14 | **72** | 56 | 72 | **yes, exact** |
| 2 | translator already resolves it as a VALUE (reverted tier 2) | — | — | 54 | **54** | 28 | 54 | **yes, exact** |
| 3 | `EF.Property` leaf | 38 | 6 | 6 | **50** | 44 | 48 | +2 |
| 4 | constructed value (tuple / anon / DTO / list) | 16 | 2 | 24 | **42** | 36 | 50 | **−8** |
| 5 | bare constant / query parameter | 30 | 10 | — | **40** | 40 | 40 | **yes, exact** |
| 6 | `Nullable.HasValue` / `.Value` | 10 | — | 28 | **38** | 36 | 38 | **yes, exact** |
| 7 | `Contains` over a client collection | 18 | 18 | — | **36** | 36 | 36 | **yes, exact** |
| 8 | `Coalesce` (`??`) | 2 | 22 | 8 | **32** | 18 | 32 | **yes, exact** |
| 9 | `Add` (string concat / arithmetic) | 10 | 4 | 18 | **32** | 16 | 32 | **yes, exact** |
| 10 | entity equality (`o == someOrder`) | 30 | — | — | **30** | 22 | 34 | −4 |
| 11 | other client / BCL method call | 8 | — | 22 | **30** | 30 | 30 | **yes, exact** |
| 12 | `Conditional` (`?:`) | 2 | 10 | 14 | **26** | 26 | 26 | **yes, exact** |
| 13 | other arithmetic / comparison operator | 16 | 4 | 2 | **22** | 22 | 18 | +4 |
| 14 | unary `Negate` | — | — | 18 | **18** | 8 | 18 | **yes, exact** |
| 15 | `Not` over a non-native operand | — | 18 | — | **18** | 18 | 18 | **yes, exact** |
| 16 | `Equals(...)` method | 16 | — | — | **16** | 16 | 16 | **yes, exact** |
| 17 | `GetType` / type test | 8 | — | — | **8** | 8 | 8 | **yes, exact** |
| 18 | array literal | 2 | — | 4 | **6** | 6 | 10 | −4 |
| 19 | other member access | — | 2 | 4 | **6** | 4 | 6 | **yes, exact** |
| 20 | `EF.Functions.Like` | 4 | — | — | **4** | 4 | 4 | **yes, exact** |
| | **total** | **260** | **104** | **216** | **580** | **474** | ≈588 | −8 |

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
| `EF.Property` 6, `Convert` 4, other member 2 | 12 | translator only (result is a field ref) |

**Verdict.** Stream 1 is **not one capability, it is two**:

- **Capability A — expression breadth in `TranslateOperand` / `TryResolveMember`.** ~476–488 cases (predicate
  260 + projection 216 + the ≤12 field-shaped sort cases). The spec's "one place has to learn them" is **exactly
  right for this part**, and the predicate↔projection identity is stronger than the spec claims: it is the same
  *method*, not two similar ones.
- **Capability B — computed sort keys.** ≥92 cases. Not a `MongoExpressionTranslator` change at all. It is not
  named anywhere in the merge plan.

This does not invalidate merging the 368 and 220 buckets — the merge is correct for the 480-odd cases that
matter most — but **a stream-1 implementation plan written as "20 translator slices" will silently under-deliver
by ~92 cases unless capability B is scheduled as its own slice.**

---

## 5. Stream 1's realistic yield is 474, not 588

MEASURED. Of the 582 cases whose first decline site is one of the three stream-1 sites and which carry at least
one stream-1 feature:

| | cases | closes when |
|---|---:|---|
| exactly one decline site, one feature | **474** | that feature's slice ships |
| co-blocked at `TryTranslateSetOperation:2448` | 32 | **stream 1 + stream 2** (set ops) |
| co-blocked at `TranslateDistinct:1318` | 26 | **stream 1 + stream 2** (`Distinct`) |
| two stream-1 features at the same site | 32 | two stream-1 slices |
| co-blocked at the `ThenBy` arm (`:128`) only | 2 | within stream 1 |
| co-blocked at `BindAggregateOrFallback:1368` | 4 | **stream 1 + stream 2** (scalar aggregate) |
| additionally blocked by joins / composite-PK / entity leaf | **12** | **never, this release** (all deferred) |

**There is no double-count between streams 1 and 2** — the two buckets are partitioned by *first* decline site,
so the 62 cases above are counted once, in stream 1. But there is an undocumented **dependency**: 62 of stream
1's cases convert only once stream 2 has also landed, and 12 do not convert at all. The plan's §7 re-measurement
checkpoint should therefore expect:

- **after stream 1 alone: ≈474**, not 588 (and 474 is itself an upper bound — sole-cause means nothing else
  declined at *population* time; the lowerer or renderer can still decline, as the 81 no-gate-site cases show);
- **after streams 1 + 2 together: up to ≈570** from stream 1's bucket.

If the checkpoint measures ~474 after stream 1, **that is the expected result, not a shortfall** — reading it as
one and pulling joins back in would be an error. INFERRED from the MEASURED table above.

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

Sized by **sole-cause** (the yield if that slice ships alone) with the group **total** as the upper bound.
Ordered by yield-per-unit-of-work, with the two hard prerequisites first.

### Slice 0 — file split (0 cases)
The `partial class` move of §6. Pure mechanical; gate is a green three-EF-version build and a zero-delta spec
sweep.

### Slice B — computed sort keys (≥92 cases enabled, 0 delivered alone)
`$set`/`$addFields` + `$sort` + `$unset`, a new `MongoAddFieldsStage`, `RenderSort` accepting a non-field
`KeySelector`, and `NativeSlotPopulator`'s `OrderBy`/`ThenBy` arms calling `TryTranslate`/`TryTranslateValue`
instead of only `TryTranslateField`. **Delivers nothing on its own** — it is the multiplier that lets the sort
column of slices 5, 8, 9, 12, 13 count. Schedule it early or accept that ~92 of the 588 will not convert.
UNVERIFIED: whether a synthetic sort field survives the whole-entity DOM/streaming shapers untouched; that is
the spike this slice must open with.

### Capability-A slices, by measured yield

| slice | feature | total | **sole-cause** | notes |
|---|---|---:|---:|---|
| A1 | cast / `Convert` operand | 72 | **56** | `TranslateOperand`'s `Convert` branch. Highest single yield. Narrowing casts are the documented divergence risk — do not simply relax the guard. |
| A2 | `EF.Property` leaf | 50 | **44** | `TryResolveMember` / `TryGetMemberOrEFProperty`. Lowest-risk slice on the board — pure resolution, no new node kind, lands in all three positions. **Best first slice after slice 0.** |
| A3 | bare constant / query parameter | 40 | **40** | 100% sole-cause. Predicate/sort only; the projection half is A4. |
| A4 | VALUE_OK / reverted tier 2 (projection) | 54 | **28** | **Has a recorded prerequisite** — see the step-3a note in `Query/AGENTS.md`: tier 2 was built, measured and reverted because the late-fallback path inherits the driver's bare `$size`. Do not re-attempt without that fixed. |
| A5 | `Nullable.HasValue` / `.Value` | 38 | **36** | `TryResolveMember` peel. Pairs naturally with A2. |
| A6 | client-collection `Contains` | 36 | **36** | 18 predicate (existing `MongoInExpression`), 18 sort (**needs slice B**). |
| A7 | constructed value (tuple / anon / DTO / list) | 42 | **36** | Predicate half is tuple comparison; projection half is a nested construction. Check whether these are really one slice before committing. |
| A8 | other client / BCL method call | 30 | **30** | Long tail: `ToString`, `Abs`, `IndexOf`, `ToList`/`ToArray`/`AsEnumerable`, `FirstOrDefault`, a client method. Split per method if it does not fit one slice. |
| A9 | `Conditional` (`?:`) | 26 | **26** | New `$cond` node ⇒ renderer + `IsQueryDialectRenderable` + negator, all three. 10 of 26 need slice B. |
| A10 | entity equality | 30 | **22** | Decompose to key comparison; all 30 in `NorthwindMiscellaneous`. No new node kind. |
| A11 | other arithmetic / comparison operator | 22 | **22** | |
| A12 | `Coalesce` (`??`) | 32 | **18** | New `$ifNull` node. **22 of 32 need slice B** — without it this slice yields ~10. |
| A13 | `Not` over a non-native operand | 18 | **18** | **All 18 are sort position** — yields **zero** without slice B. |
| A14 | `Add` (string concat / arithmetic) | 32 | **16** | `$concat` for the string case. |
| A15 | `Equals(...)` method | 16 | **16** | Map to `Equal`; no new node kind. Cheap. |
| A16 | `GetType` / type test | 8 | **8** | Reuse `TranslateOfType`'s discriminator machinery. |
| A17 | unary `Negate` | 18 | **8** | Projection only. |
| A18 | array literal | 6 | **6** | |
| A19 | other member access | 6 | **4** | |
| A20 | `EF.Functions.Like` | 4 | **4** | Reuse `MongoRegexExpression`. All 4 in one class. |

**Totals: 580 upper bound, 474 sole-cause.** Slices A1–A5 alone are **204 sole-cause** — 43% of the realistic
yield in five slices, none of which needs slice B.

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
- The field-shapedness split of the 104 sort cases (92 / 12) is INFERRED from the feature classification plus
  `RenderSort`'s contract; it was not measured by mutating `RenderSort`.
- Whether the `EF.Property` fix in `TryResolveMember` really lands in all three positions was established by
  READING the call graph, not by shipping it.
- Whether the 4 ambiguous `NorthwindAggregateOperatorsQueryMongoTest` cases are composite-PK or breadth (the ±4
  in §2). Resolvable by instrumenting `TryResolveMember`'s composite-PK guard specifically.
- Nothing here re-derived §9.1's per-test-class table (§7.1), the `Native`-axis MQL baselines, or the (b)/(c)
  partitions beyond their totals.
