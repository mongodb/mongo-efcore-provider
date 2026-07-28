# EF-322 (stream 1, slice A1) — the cast / `Convert`-operand breadth spike

*Run 2026-08-08 in two throwaway worktrees at `c8a8a5b4` — one carrying an env-gated PROTOTYPE plus a probe
harness (§2–§6), one PRISTINE apart from an instrumentation hook (§7). Both created, used and removed;
`git worktree list` verified before and after — the three `.claude/worktrees/agent-*` worktrees belong to other
sessions and were neither created nor touched. The main tree finished with only this file added (plus the
owner's pre-existing uncommitted one-character edit to `2026-08-07-native-query-merge-plan-design.md`, left
untouched and out of the commit). Inputs: `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`
§3 row 1, §4.2, §4.3, §5.1, §7 row A1; `docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md` §5.1.1 and
**§8** (the open question this spike was chartered to settle); `Query/AGENTS.md`'s slice-B note;
`MongoExpressionTranslator.TranslateOperand` / `.TranslateComparison` / `.TryTranslateField` /
`.HasNumericConvert`, `NativeProjectionBinder.TryTranslateLeaf`, `MongoAggregationExpressionRenderer`,
`MongoQueryLanguageRenderer.IsQueryDialectRenderable`.*

**Tagging convention, applied strictly.** Every claim below is one of:
**MEASURED** (produced by a run this session; the method is §9 and the numbers are reproducible) ·
**READ** (established by reading source at `c8a8a5b4`; no execution) ·
**INFERRED** (drawn from MEASURED/READ facts, not itself observed) ·
**UNVERIFIED** (not established — said so explicitly).
An answer reached by reading source is READ, never MEASURED.

**Trap compliance, stated up front.** (a) Every routing claim is a `MongoQueryMode.NativeOnly` run that
succeeds or throws — never an MQL shape, which cannot prove a query went native. Emitted MQL is quoted
throughout, but only ever as *description*, never as the routing signal. (b) **Every claim that depends on the
prototype was A/B-controlled from ONE build**: the prototype is behind four env gates (`MONGODB_EF_A1` level,
`MONGODB_EF_A1_SORTFIX`, `MONGODB_EF_A1_LEAF`, `MONGODB_EF_A1_FALLTHROUGH`) and the same binaries were run
eleven ways. **The gate is demonstrably live**: with `MONGODB_EF_A1=0` the whole spec suite reproduces the
documented base exactly (`NativeOnly` **2473 / 2120 / 17**, `Native` **4593 / 0 / 17**) and the probe harness's
`NativeOnly` column is all-throws for the cast shapes; with the gates on, 28 spec cases flip and the probe's
`NativeOnly` column turns green shape by shape. Nothing below rests on a change that was not proved active.
(c) Every count-based claim was cross-checked against the failure MESSAGE, not the count alone — §5 exists
because for one configuration the count says the opposite of the truth, exactly as slice B's did.
(d) `MONGODB_URI` and `ATLAS_URI` were both unset, so TestContainers booted its own
`mongodb/mongodb-atlas-local` per test process. (e) Counts quoted from other documents are labelled CITED and
are never re-stated as this spike's own measurements.

---

## Headline — nine findings, in order of how much each changes A1's plan

1. **THERE IS A LIVE SILENT-WRONG-ORDER DEFECT, and it is A1's to fix. Slice B's §8 is settled RED.**
   `OrderBy(x => (int)x.D)` over a `double` is native today and sorts by the **raw double**. On a fixture that
   discriminates (D = 1.6, 1.4, 2.5, −1.5, 0.5) native returns **`d,e,b,a,c`** while explicit `DriverLinq` and
   in-memory LINQ **both** return `d,e,a,b,c`. Two stronger, non-tie-dependent instances exist:
   `OrderBy(x => (uint)x.I)` and `OrderBy(x => (short)x.I)` are genuine order REVERSALS (not ties), where native
   silently returns the raw-`int` order and the driver's own LINQ provider refuses to translate the shape at
   all. **MEASURED, all three.** It does **not** predate the native work — `Unwrap` and `TryTranslateField` were
   introduced in the same commit, `1d5580e3` (EF-323, the native foundation), and
   `Infrastructure/MongoQueryMode.cs` does not exist at `v10.0.2` / `v9.1.2` / `v8.4.2` (READ, `git ls-tree`;
   `v10.0.2` confirmed the latest release via `gh release list`), so the whole native path is **UNRELEASED**.
   **Recommended disposition: fix inside A1, as its first task, no `BREAKING-CHANGES.md` entry.** §4.

2. **The recorded driver "inconsistency" is REAL but MISDESCRIBED, and the correction removes A1's stated
   blocker.** `TranslateOperand`'s XML remark says the driver renders a numeric cast on a comparison operand as
   `$toDouble` but "simply drops it" on an arithmetic operand — "an inconsistent, shape-dependent rule" whose
   reproduction "would require re-deriving the driver's numeric-promotion logic". MEASURED: the driver drops a
   **WIDENING** cast on an arithmetic operand (`(double)c.I + c.D` → `{$add: ["$I", "$D"]}`) and **RENDERS** a
   **NARROWING** one in the very same position (`(int)c.D + c.I` → `{$add: [{$toInt: "$D"}, "$I"]}`). That is not
   an inconsistency — **it is exactly the rule the provider already implements** for `allowNumericWidening:
   true`. The only genuine difference is that on the COMPARISON path the driver renders even a widening cast
   explicitly. **No promotion logic needs re-deriving: the rendering is a one-to-one map from the target CLR
   type to one of four MQL operators.** §3.

3. **The admissible set is bounded by MQL itself, not by taste: `$toInt`, `$toLong`, `$toDouble`, `$toDecimal`
   and nothing else.** For a target of `short`, `uint` or `float` the **driver's own LINQ provider throws
   `ExpressionNotSupportedException`** — MEASURED in predicate, sort and projection position alike. MQL has no
   `$toShort`/`$toUInt`/`$toFloat`. So "keep declining" for those targets is not a coverage gap A1 chose; it is
   the same boundary the oracle has. §3.2.

4. **A1's realistic spec yield is 28 cases, 30 after re-baselining — not 56.** MEASURED by prototype A/B,
   message-transition on both axes: the **widening** relaxation delivers **+18**, an **enum/char/boxing**
   arm a further **+10**, a comparison-branch **fall-through** a further **+2 (masked by its own MQL
   re-baseline)**, and the `$expr` narrowing-convert node, the projection-leaf gate and the sort fix deliver
   **ZERO each**. Zero `Passed→Failed` throughout; the first 28 need **no** baseline re-basing at all. §5.

5. **The highest-yield arm is at a decline site NO existing document names.** All 18 of the first-wave wins come
   from `TranslateComparison`'s query-native branch and its private `HasNumericConvert` guard — not from
   `TranslateOperand`'s `Convert` branch, which every A1 write-up so far points at (the stream-1 spike's §3 row 1
   fix column, §4.2's "literally the same lines", and §7 row A1). MEASURED: over the whole EF10 spec suite
   `TranslateOperand`'s `Convert` branch declines **40** times and `HasNumericConvert` declines **140** times, and
   the 40 are almost entirely reference-type/DTO/boxing converts, not numeric casts. **An A1 plan written against
   `TranslateOperand` alone would deliver 0 of the 28.** §7.

6. **The single biggest hazard is not the cast — it is the CONSTANT beside it, and it is silent wrong data under
   the default `Native` mode.** Absorbing a widening cast on the member side makes the comparison happen in the
   cast's type, but the constant is still serialized through the **property's** serializer:
   `Where(x => (double)x.I >= 2.5)` emitted `{"I": {"$gte": 2}}` and returned **`b,c,d,e` where `b,c,e` is
   correct** (both CLR and driver-LINQ). MEASURED, with the prototype live. Switching the constant to the
   comparison type fixes it exactly (`{"I": {"$gte": 2.5}}`, correct rows, and the emitted MQL then matches the
   driver byte for byte) — but a **blanket** switch is itself wrong: it broke 5 spec and 23 functional cases,
   every one an enum-as-string or value-converted property. The rule has to be conditional. §6.

7. **The narrowing guard's two conjuncts are NOT equally movable, and the distinction is precise.** The
   *position* conjunct (`allowNumericWidening`) **can** move — the driver renders a widening cast explicitly on
   the comparison path, so admitting it there is driver-identical. The *widening* conjunct
   (`IsWideningNumericConvert`) **cannot be deleted**; it can only be **repurposed** from "admit ⇒ unwrap" to
   "admit ⇒ unwrap (widening) / admit-with-an-explicit-`$toX`-node (narrowing, renderable target) / decline
   (everything else)". Deleting it *is* the sort-position defect of finding 1, arriving through the other door.
   §8.

8. **The projection column of the cited 72 needs a SECOND gate, and delivers nothing measurable.** A cast leaf
   is a `UnaryExpression`, and `NativeProjectionBinder.TryTranslateLeaf` pre-filters on
   `leafExpression is MemberExpression` (READ), so `TryTranslateField`'s `Unwrap` is **unreachable** for a
   projection leaf — the projection position does **not** carry finding 1's defect (MEASURED: `NativeOnly`
   throws for wrapped and bare cast leaves alike, and the fallback returns correct values). Widening that gate
   is the same shape of change slice A2 needed for `EF.Property`. MEASURED yield of doing so: **0 spec cases.**
   §4.3, §5.

9. **A1 must re-baseline two existing functional decline tripwires, and one deliberately locked routing pin.**
   `NativeExprComparisonTests.NativeOnly_cast_in_field_to_field_comparison_throws` and
   `..._cast_in_arithmetic_operand_throws` pin today's decline and go red the moment A1 ships (correctly — they
   are tripwires meant to be flipped deliberately). The enum arm additionally flips
   `NativeGateRoutingTests.A_enum_as_string_where_equals_routing`, whose comment locks the fallback by name; its
   parity sibling stayed green, so results were still correct on that fixture. MEASURED. §5.3.

---

## 1. What was already established, and what this spike had to settle

READ, not re-derived: `TranslateOperand`'s `Convert` branch rejects a type-changing cast unless
`allowNumericWidening && IsWideningNumericConvert(from, to)`; `TryTranslateField` calls `Unwrap`, which strips
**any** `Convert`/`ConvertChecked` with no widening check, so a cast in SORT position is already stripped today;
`TryTranslate` likewise `Unwrap`s its top-level node.

CITED, not measured here: the A1 row is **72 total (predicate 50 / sort 8 / projection 14), 56 sole-cause, 6 of
the 56 slice-B-dependent** (stream-1 spike §3 row 1, §5.1, §7 row A1); its `Convert` row is labelled by the
*operand* rule and classifying by minimal node alone puts 39 of the 50 predicate cases under "other operator"
(that spike's finding 5); slice A5 was sized 38/36 and converted **ZERO** (`docs/native-query-status-EF-322.md`
§2). The brief's instruction — *do not inherit 72/56* — is honoured: §5 re-derives the yield by execution.

---

## 2. A THIRD decline site, found by reading — and it is where A1's yield actually lives

READ. There are **three** sites at which a cast makes a query non-native, not the one every A1 write-up names:

| # | site | what it sees | disposition today |
|---|---|---|---|
| **A** | `TranslateOperand`'s `Convert` branch | a cast on an operand of a field-to-field / arithmetic comparison, or of a computed VALUE leaf | `return null` |
| **B** | `TranslateComparison`'s query-native branch → `HasNumericConvert` | a cast on the MEMBER side of a `member <op> constant/parameter` comparison | `return null` — **for the WHOLE comparison**, with no fall-through to the `$expr` path below it |
| **C** | `TryTranslateField` → `Unwrap` | a cast in SORT position (or on a field-shaped projection leaf) | **silently stripped** |

Site B is the one no A1 document names, and §7 measures it as carrying **3.5× site A's decline volume**. Its
`return null` (rather than a fall-through) is structural and load-bearing for A1: it means a shape site B
declines can never reach site A's `$expr` machinery, however capable that machinery becomes. Site C is finding
1's defect.

---

## 3. Question 1 — what the driver EMITS and RETURNS, per position, per cast

MEASURED. One fixture, five rows, seeded so that raw-`I` order, `(uint)I` order, `(short)I` order, raw-`D`
order and `(int)D` order are five distinct permutations (§9 gives the rows and the hand-derived orders). Every
row below is the same query object run four ways: in-memory LINQ over the same `Expression`, explicit
`DriverLinq`, default `Native`, and `NativeOnly`. **The `NativeOnly` column is the routing proof.**

### 3.1 Comparison operand, field-to-field (`$expr` position) — base tree

| probe | shape | in-memory LINQ | DriverLinq result | driver emits | Native | NativeOnly |
|---|---|---|---|---|---|---|
| P01 | `(double)x.I > x.D` (W) | `b,c,d,e` | `b,c,d,e` | `{$gt: [{$toDouble: "$I"}, "$D"]}` | same | **throws** |
| P02 | `(long)x.I > x.L` (W) | `b,c,e` | `b,c,e` | `{$gt: [{$toLong: "$I"}, "$L"]}` | same | **throws** |
| P03 | `(double)x.F > x.D` (W) | `b,d,e` | `b,d,e` | `{$gt: [{$toDouble: "$F"}, "$D"]}` | same | **throws** |
| P04 | `(decimal)x.I > x.M` (W) | `b,c,e` | `b,c,e` | `{$gt: [{$toDecimal: "$I"}, "$M"]}` | same | **throws** |
| P05 | `(int)x.D > x.I` (N) | `a` | `a` | `{$gt: [{$toInt: "$D"}, "$I"]}` | same | **throws** |
| P06 | `(int)x.L > x.I` (N) | `a,d` | `a,d` | `{$gt: [{$toInt: "$L"}, "$I"]}` | same | **throws** |
| P07 | `(short)x.I > x.S` (N) | `b,c,e` | **`ExpressionNotSupportedException`** | — | throws | throws |
| P08 | `(uint)x.I > (uint)x.IOther` | `a,b,d,e` | **`ExpressionNotSupportedException`** | — | throws | throws |
| P09 | `(float)x.D > x.F` (N) | `a,c` | **`ExpressionNotSupportedException`** | — | throws | throws |
| P13 | `(double)x.I + x.D > 5.0` (W, arith) | `b,c,e` | `b,c,e` | `{$add: ["$I", "$D"]}` — **cast dropped** | same | **throws** |
| P14 | `(int)x.D + x.I > 5` (N, arith) | `b,c,e` | `b,c,e` | `{$add: [{$toInt: "$D"}, "$I"]}` — **cast rendered** | same | **throws** |

**P13 vs P14 is the correction to the recorded remark** (finding 2): the arithmetic-operand rule is
widening-drops / narrowing-renders, which is precisely `IsWideningNumericConvert`.

**Values: in-memory LINQ, the driver and the current fallback agree on every renderable row.** So for these
shapes there is no divergence to accept — only a decline to remove.

### 3.2 The renderable set is exactly four operators

MEASURED from P07/P08/P09 plus their sort (S04/S05) and projection (J03) siblings: **every** shape whose cast
target is `short`, `uint` or `float` makes the driver's own LINQ provider throw `ExpressionNotSupportedException`
— in all three positions. The four targets that render are `int` → `$toInt`, `long` → `$toLong`, `double` →
`$toDouble`, `decimal` → `$toDecimal`. This is MQL's own boundary, not the driver's opinion.

INFERRED: A1 declining those targets therefore costs nothing against the branch's oracle — the query falls back
and the driver throws, which is what it does today.

### 3.3 Comparison operand vs a CONSTANT (query dialect) — where the driver is measurably wrong

| probe | shape | in-memory LINQ | DriverLinq | driver emits |
|---|---|---|---|---|
| P10 | `(double)x.I > 3.0` (W) | `b,c,e` | `b,c,e` | `{"I": {"$gt": 3.0}}` — cast dropped, harmless |
| P11 | `(int)x.D > 0` (N) | `a,b,c` | **`a,b,c,e`** | `{"D": {"$gt": 0}}` — cast dropped, **WRONG** |
| P12 | `(uint)x.I > 3u` | `a,b,c,e` | **`b,c,e`** | `{"I": {"$gt": 3}}` — cast dropped, **WRONG** |

MEASURED. On the query-dialect path the driver drops the cast unconditionally; for a widening cast that is
value-preserving, for a narrowing or signed/unsigned one it is not. `Native` matches `DriverLinq` on all three
today only because it falls back to it.

**Consequence for A1, stated as a decision rather than a fact:** if A1 admits a *narrowing* cast here it will
naturally emit `{$expr: {$gt: [{$toInt: "$D"}, 0]}}` (the member side is no longer bare, so
`IsQueryNativeComparison` is false), which **agrees with the CLR and disagrees with driver-LINQ**. That is a
result change under the default mode for a currently-falling-back shape. §8 recommends taking it and
documenting it; §10 records that no owner ruling exists.

### 3.4 Answer to question 1, plainly

**Yes, the recorded inconsistency is still true as an observation and FALSE as a characterisation.** The rule is:
*comparison operand ⇒ render the cast; arithmetic operand ⇒ drop it if widening, render it if narrowing;
query-dialect member-vs-constant ⇒ drop it always.* **An exact native reproduction is possible and cheap** — one
new expression node rendering `{"$toX": <operand>}` for four values of X, plus (for the query-dialect path) a
decision about whether to reproduce the driver's cast-dropping or the CLR's answer. **Nothing requires
re-deriving the driver's numeric-promotion logic**; the provider already owns that logic in
`WideningNumericConversions`, and it MEASURES as agreeing with the driver's arithmetic-operand behaviour.

---

## 4. Question 2 — the live defect, measured three ways

### 4.1 Sort position: native disagrees with BOTH oracles

MEASURED, base tree, every row a `NativeOnly` run (so routing is proven, not inferred):

| probe | sort key | in-memory LINQ | DriverLinq | **Native / NativeOnly** | verdict |
|---|---|---|---|---|---|
| S01 | `(double)x.I` (W) | `a,d,c,e,b` | `a,d,c,e,b` | `a,d,c,e,b` | agrees — widening is order-preserving |
| S02 | `(long)x.I` (W) | `a,d,c,e,b` | `a,d,c,e,b` | `a,d,c,e,b` | agrees |
| S06 | `(object)x.D` (boxing) | `d,e,b,a,c` | `d,e,b,a,c` | `d,e,b,a,c` | agrees — boxing is order-preserving |
| **S03** | **`(int)x.D` (N)** | **`d,e,a,b,c`** | **`d,e,a,b,c`** | **`d,e,b,a,c`** | **DEFECT — native alone disagrees** |
| **S04** | **`(uint)x.I`** | **`d,c,e,b,a`** | `ExpressionNotSupported` | **`a,d,c,e,b`** | **DEFECT — a genuine REVERSAL, silently** |
| **S05** | **`(short)x.I`** | **`a,d,c,b,e`** | `ExpressionNotSupported` | **`a,d,c,e,b`** | **DEFECT — a genuine REVERSAL, silently** |

The emitted native MQL for all three defect rows is a bare `{"$sort": {"D": 1}}` / `{"$sort": {"I": 1}}` — the
cast is simply gone. The driver's is a `$project`/`_key1`/`$sort`/`$replaceRoot` triple carrying `{$toInt: "$D"}`.

**S03 is the shape slice B's §8 names (1.4 and 1.6 tie at `(int)` but not raw), and it discriminates: it is a
tie-ORDER difference, which MongoDB's `$sort` does not guarantee anyway.** S04 and S05 are the load-bearing
evidence, because they are **order reversals, not ties** — no stability assumption is involved. Both were built
for this spike precisely because the tie-based repro is too weak to carry the finding on its own.

### 4.2 Does it predate the native work? No.

READ. `git log -S` shows `Unwrap` and `TryTranslateField` first appearing in the SAME commit — `1d5580e3`,
*"EF-323: Native query-translation foundation (native filter / sort / paging)"*. `git ls-tree` shows
`src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs` **absent at `v10.0.2`, `v9.1.2` and
`v8.4.2`**, and `gh release list` confirms `v10.0.2` is the latest published release. So there is no released
version in which this sort path exists at all: **the defect is a native-path defect, introduced with the native
path, never shipped.**

### 4.3 The projection leaf does NOT have the same defect

MEASURED. A cast projection leaf — wrapped (`new { V = (int)x.D }`, J02), bare (`Select(x => (int)x.D)`, J04),
widening (J01, J07) or unsigned (J03) — **throws under `NativeOnly`**, i.e. it is not native, and under `Native`
the fallback returns values identical to in-memory LINQ. READ explains why: `TryTranslateLeaf`'s plain-field
branch pre-filters on `leafExpression is MemberExpression` before it ever calls `TryTranslateField`, so site C's
`Unwrap` is unreachable from a projection leaf.

### 4.4 The fix, and that it works

MEASURED, prototype `MONGODB_EF_A1_SORTFIX=1` (an order-aware unwrap: strip only a widening numeric convert or a
boxing convert to `object`; otherwise leave the `Convert` in place so `TryResolveMember` declines it and slice
B's `TryTranslateValue` fall-through takes over):

| probe | with the fix + a renderable `$toX` | emitted MQL |
|---|---|---|
| S03 | **`d,e,a,b,c`** — matches in-memory LINQ AND DriverLinq | `{"$set": {"__sort0": {"$toInt": "$D"}}}, {"$sort": {"__sort0": 1}}, {"$unset": ["__sort0"]}` |
| S04 | **declines** (`NativeOnly` throws; fallback then throws, loudly) | `aggregate([])` |
| S05 | **declines** (same) | `aggregate([])` |
| S01/S02/S06 | unchanged, still native, still correct | plain `$sort` |

So the fix rides entirely on slice B's already-shipped `$set`/`$sort`/`$unset` machinery, needs no new stage, and
turns two silent wrong-order shapes into loud failures (which is what the driver does for them too).

**Spec cost of the sort fix: ZERO on both axes** (§5). **Functional cost: zero.**

### 4.5 Answer to question 2, plainly

**Yes — there is a silent-wrong-order defect on the native path today, for a narrowing or signed/unsigned cast
in sort position; it is unreleased, it is A1's subject, and it should be fixed as A1's first task.**

---

## 5. Question 4 — the sizing, re-derived by execution, and the A5 failure mode checked head-on

### 5.1 The A/B matrix

**Twenty** full EF10 specification runs, 4610 tests each, from **three successive prototype builds** — build 1
carried the widening/`$toX`/sort gates, build 2 added the enum, projection-leaf and fall-through gates, build 3
the constant-serialization variant. **Every comparison in the table is either within one build, or crosses a
build boundary that is controlled**: build 2's gates-off run (`base2`) reproduces build 1's gates-off run
**byte-identically on both axes** (`NativeOnly` 2473/2120/17, `Native` 4593/0/17), which is also the documented
base, so the harness is measuring the right thing and the two builds are interchangeable at level 0.

| config | gates | `NativeOnly` P/F/S | Δ `Failed→Passed` vs base | `Passed→Failed` | `Native` axis P/F/S |
|---|---|---|---|---:|---:|---|
| **base** | `A1=0` | **2473 / 2120 / 17** | — | — | **4593 / 0 / 17** |
| L1 | `A1=1` (widening) | 2491 / 2102 / 17 | **+18** | 0 | 4593 / 0 / 17 |
| L2 | `A1=2` (+ `$expr` `$toX` node) | 2491 / 2102 / 17 | +18 (**+0** over L1) | 0 | 4593 / 0 / 17 |
| L2+sort | `A1=2`, `SORTFIX=1` | 2491 / 2102 / 17 | +18 (**+0**) | 0 | 4593 / 0 / 17 |
| **L3** | `A1=3` (+ enum/char/boxing), `SORTFIX=1` | **2501 / 2092 / 17** | **+28** (**+10**) | 0 | **4593 / 0 / 17** |
| L2+leaf | `A1=2`, `SORTFIX=1`, `LEAF=1` | 2491 / 2102 / 17 | +18 (**+0**) | 0 | 4593 / 0 / 17 |
| L2+FT | `A1=2`, `SORTFIX=1`, `FALLTHROUGH=1` | 2491 / 2102 / 17 | +18 (**+0 raw, +2 MASKED**) | 0 | 4591 / **2** |
| **all-in** | `A1=3`, `SORTFIX`, `LEAF`, `FALLTHROUGH` | 2501 / 2092 / 17 | **+28 (+2 masked = 30)** | 0 | 4591 / **2** |
| L4 | all-in-minus-FT + blanket constant re-serialization | 2496 / 2097 / 17 | +23 (**−5 vs L3**) | **5 wrong-data** | 4588 / **5** |

**Compared as SETS with MESSAGES, not counts** (§9). Every Δ above is a `Failed→Passed` set difference;
`Passed→Failed` is empty everywhere except L4.

### 5.2 What each arm actually buys

- **L1, the widening relaxation at site B — 18 cases, 9 tests, all in `NorthwindWhereQueryMongoTest`:**
  `Where_short_member_comparison`, `Where_ternary_boolean_condition_{true,false,with_another_condition,with_false_as_result_true}`,
  `Where_simple_closure_via_query_cache_nullable_type{,_reverse}`,
  `Where_method_call_nullable_type{,_reverse}_closure_via_query_cache` (× async). **Zero MQL baselines moved** —
  because for this shape native's emission is byte-identical to the driver's (`{"Quantity": 10}` either way).
  That is unusual and worth planning around: this arm needs **no** `EF_TEST_REWRITE_BASELINES` pass.
- **L3, the enum/char/boxing arm — 10 more:** 8 × `Mapping.BuiltInDataTypesMongoTest`
  (`Can_compare_enum_to_constant`, `Can_compare_enum_to_parameter`, `Can_query_using_any_data_type`,
  `..._shadow`, `..._nullable_shadow`, `Can_query_using_any_nullable_data_type`, `..._as_literal`,
  `Can_query_with_null_parameters_using_any_nullable_data_type`) plus
  `NorthwindWhereQueryMongoTest.Where_compare_with_both_cast_to_object` (× async). Also zero baseline movement.
- **L2, the `$expr` `$toX` node — 0 cases.** The capability is real (§3.1's `NativeOnly` column turns green for
  P01–P06, P13, P14 under the prototype) and it is what finding 1's sort fix needs; it just converts no
  specification case, because the suite's cast population sits at site B, not site A.
- **The projection-leaf gate — 0 cases.**
- **The sort fix — 0 cases, 0 regressions.** Pure correctness.
- **The fall-through (site B declines ⇒ fall through to `$expr` instead of `return null`) — 2 cases, MASKED.**
  Raw counts are identical to L2's; the message transition shows
  `NorthwindWhereQueryMongoTest.Decimal_cast_to_double_works` (× async) moving from
  `NativeTranslationNotSupportedException` to an `AssertMql` mismatch (`{"UnitPrice": {"$gt": …}}` →
  the `$expr` form). This is the **exact slice-B trap**: judged on the pass count alone the fall-through
  delivers nothing. It delivers 2 after re-baselining.

### 5.3 Regressions the prototype produced — all three are deliberate tripwires

MEASURED, EF10 functional suite, A/B from one build (base **2705 passed / 0 failed / 52 skipped**):

| config | failures | which |
|---|---:|---|
| L2 + sort fix | 2 | `NativeExprComparisonTests.NativeOnly_cast_in_field_to_field_comparison_throws`, `..._cast_in_arithmetic_operand_throws` — the existing decline pins, flipped because the shape now goes native |
| L3 (+enum/char/boxing) | 3 | the two above **plus** `NativeGateRoutingTests.A_enum_as_string_where_equals_routing`, whose comment locks the fallback by name. Its parity sibling `A_enum_as_string_where_equals_parity` stayed GREEN, so results were still correct on that fixture |
| L4 (blanket constant re-serialization) | 23 | genuine wrong data — see §6 |

### 5.4 Does A1 carry the A5 failure mode? Measured — and the answer is "a different one"

A5's failure mode is *the classifier descends to the minimal failing subtree and cannot see that the ENCLOSING
construct is also unsupported*. For A1 the enclosing construct **is** a comparison, and A1 is about
comparisons — so the literal A5 mechanism does not apply. MEASURED at site B: of **140** declining occurrences
across the whole suite, **72 are widening** (the shape L1 admits), yet L1 converts **18 cases**. The gap is not
an enclosure above the cast; it is that the same handful of predicates is translated many times and that most of
the occurrences sit in queries blocked elsewhere in the same query (joins, `GroupBy`, projections).

**Two things a future reader must not do with §7's occurrence counts.** They are per-translation-call, not per
case — `(Convert(o.Quantity, Int32) == 10)` alone accounts for 32 of them while
`Where_short_member_comparison` is 2 cases. They must not be summed with, or compared row-for-row against, the
stream-1 spike's case counts. **The A/B in §5.1 is the case count; §7 is only a shape census.**

### 5.5 Answer to question 4, plainly

**Realistic expected yield: 28 specification cases with no re-baselining, 30 with a two-case
`EF_TEST_REWRITE_BASELINES` pass** — against a CITED 56 sole-cause / 72 total. Basis: direct prototype A/B,
message-transition on both axes, zero `Passed→Failed`. **Realization ≈ 50%** — far better than A5's 0, and the
shortfall is located rather than guessed: the projection column (CITED 14) and the sort column (CITED 8) each
deliver **zero**, and the predicate column's remainder sits behind blockers outside A1.

---

## 6. Question 3 — the admissible set, and the constant hazard that governs it

### 6.1 The partition the brief asks for

**(a) Reproducible exactly and safe to admit.** MEASURED: native == driver == in-memory LINQ on every row.
- A cast to `int` / `long` / `double` / `decimal` on a **field-to-field or arithmetic comparison operand**,
  rendered `{$toX: …}` (P01–P06, P13, P14 — all green under the prototype's `NativeOnly`).
- A cast to those four targets on a **computed VALUE leaf** (J05, J06; J01/J02/J04/J07 once the leaf gate opens).
- A **widening** numeric cast on the member side of a `member <op> constant` comparison, **provided the constant
  is handled per §6.2** (the 18 wins).
- An **enum → its own underlying type** convert, and **`char` → `int`**, and a **boxing convert to `object`**
  (the 10 wins) — value-identical by construction.
- In SORT position: a widening numeric convert and a boxing convert, stripped as today (S01, S02, S06).

**(b) Reproducible but divergent from in-memory LINQ in the accepted EF-359 sense (native == driver, both ≠
CLR).** MEASURED, and it is a SMALL set:
- Nothing in the `$expr` positions — every renderable row there agrees with the CLR too.
- **Out-of-range conversions**: `$toInt` errors where C# unchecked wraps, `$toInt` of a double truncates toward
  zero (matching C# on every value probed). Not exercised at the boundary here — **UNVERIFIED** (§10).
- If A1 chooses to reproduce the driver's cast-DROPPING on the query-dialect narrowing path (P11, P12), those
  rows become (b). **Recommendation: do not** — see §8.

**(c) Not reproducible; must keep declining.** MEASURED:
- Any cast whose target is not one of the four `$toX` targets — `short`, `ushort`, `uint`, `ulong`, `byte`,
  `sbyte`, `float`, `char`-as-target. The driver refuses these too, so the decline matches the oracle.
- In SORT position, any cast that is not order-preserving (§4.4) — decline rather than strip.
- Reference-type / DTO converts (`CustomerIdAndCityDto -> CustomerIdDto`, `IQueryable -> IEnumerable`), which
  §7 shows dominate site A's declines and which are not casts in A1's sense at all.

### 6.2 The constant hazard — the most actionable finding in this section

MEASURED with the prototype live, and **it is silent wrong data under the default `Native` mode**:

| probe | shape | in-memory LINQ | DriverLinq | prototype L1–L3 Native | emitted |
|---|---|---|---|---|---|
| P15 | `(double)x.I >= 2.5` | `b,c,e` | `b,c,e` | **`b,c,d,e`** | `{"I": {"$gte": 2}}` |
| P17 | `(decimal)x.I >= 2.5m` | `b,c,e` | `b,c,e` | **`b,c,d,e`** | `{"I": {"$gte": 2}}` |
| P10 | `(double)x.I > 3.0` | `b,c,e` | `b,c,e` | `b,c,e` | `{"I": {"$gt": 3}}` — right by luck |
| P16 | `(long)x.I > 2147483648L` | `∅` | `∅` | `∅` | `{"I": {"$gt": 2147483648}}` — right |

`TranslateComparison` serializes the constant with `forSerialization: leftProperty` — the **stored** type. Once
a widening cast is absorbed, the comparison happens in the **cast's** type, and a fractional constant is
truncated to the integral stored type. The 18 wins never fire it only because their constants are integral.

**The remedy works, and a blanket version of it does not.** With the constant serialized in the comparison's
type instead (`BsonValue.Create`), P15/P17 return `b,c,e` and the emitted MQL becomes byte-identical to the
driver's (`{"I": {"$gte": 2.5}}`, `{"I": {"$gt": 3.0}}`). But applied to **every** convert layer it cost 5 spec
Native-axis failures (`BuiltInDataTypesMongoTest.Can_query_using_any_*` — *"Sequence contains no elements"*,
i.e. wrong rows) and 23 functional failures, essentially all
`ValueConverterTests.Enum_can_deserialize_and_query_from_string*` and
`NativeGateRoutingTests.A_enum_as_string_where_equals_parity` — because the **enum/identity-like** arm of §6.1(a)
*requires* the property serializer (that is how an enum-as-string constant is rendered).

**So the rule A1 must implement is conditional, and both halves are measured:** serialize the constant in the
COMPARISON's type for a **numeric widening** cast over a **default-serialized** property; keep the PROPERTY's
serializer for an enum/char/boxing convert. **UNVERIFIED**: whether "default-serialized"
(`NativeGroupByBinder.HasDefaultKeySerialization`, the predicate five other sites already use) is exactly the
right conjunct — no fixture was built that separates it from "is an enum".

---

## 7. The shape census — site A and site B, over the whole spec suite

MEASURED, in the SECOND worktree: **pristine apart from an instrumentation hook** on sites A and B and on the
three translator entry points. One full EF10 spec run on the default `Native` axis returned **4593 passed / 0
failed / 17 skipped** — the documented pristine baseline, so the hook perturbs nothing.

*(Basis caveat, so this is not misread as a re-derivation of the CITED 72. These are declining **occurrences**
per translation call, not cases; async twins and repeated translations of the same predicate each count. What
this population can answer, and all it is used for here, is the SHAPE question.)*

### 7.1 Site A — `TranslateOperand`'s `Convert` branch: 40 occurrences, almost none of them numeric

| cast | n | what it is |
|---|---:|---|
| `CustomerIdAndCityDto -> CustomerIdDto` | 12 | a DTO upcast in a projection — not a numeric cast at all |
| `Int32 -> Object` | 10 | boxing, mostly `o.Outer.OrderID` / a transparent identifier (a JOINS blocker) |
| `IQueryable`1 -> IEnumerable`1` | 4 | a correlated subquery being adapted |
| `EmailTemplateType -> EmailTemplateTypeDto` | 4 | an enum-to-DTO upcast |
| `DateTime -> Object` | 4 | boxing over a transparent identifier |
| `Int64 -> Int16` | 2 | the ONLY genuine numeric narrowing at this site |
| `DayOfWeek -> Int32` | 2 | enum to underlying |
| `NorthwindMiscellaneousQueryMongoTest -> …TestBase\`1` | 2 | a test-fixture constant boxed to its base type |

12 + 10 + 4 + 4 + 4 + 2 + 2 + 2 = **40**. **Two occurrences of a genuine numeric narrowing, in the whole
suite.** That is why L2 converts zero.

### 7.2 Site B — `HasNumericConvert`: 140 occurrences, and this is where A1 lives

| cast pair | n | class | admitted by |
|---|---:|---|---|
| `Int16 -> Int32` | 40 | WIDENING | L1 |
| `UInt16 -> Int32` | 14 | WIDENING | L1 |
| `SByte -> Int32` | 6 | WIDENING | L1 |
| `Byte -> Int32` | 6 | WIDENING | L1 |
| `UInt32 -> Int64` | 4 | WIDENING | L1 |
| `Single -> Double` | 1 | WIDENING | L1 |
| `Int16 -> Int64` | 1 | WIDENING | L1 |
| `EmailTemplateType -> Int32` | 8 | enum → underlying | L3 |
| `Enum{8,16,32,64,S8,U16} -> Int32/Int64` | 36 | enum → underlying | L3 |
| `EnumU64 -> UInt64`, `EnumU32 -> UInt32` | 12 | enum → underlying | L3 |
| `IdentificationMethod -> Int32` | 2 | enum → underlying | L3 |
| `Char -> Int32` | 6 | char → int | L3 |
| `String -> Object` | 2 | boxing | L3 |
| `Decimal -> Double` | 2 | **non-widening numeric** | needs the fall-through (the `Decimal_cast_to_double_works` pair) |

**72 WIDENING + 68 OTHER = 140.** Re-summed from the table rather than restated: WIDENING = 40+14+6+6+4+1+1 =
**72**; OTHER = 8+36+12+2+6+2+2 = **68**.

The top declining bodies, for shape: `(Convert(o.Quantity, Int32) == 10)` ×32,
`(((o.ProductID % 23) == 17) AndAlso (Convert(o.Quantity, Int32) < 10))` ×48 at the predicate entry point,
`(Convert(p.UnitsInStock, Int32) >= 20)` ×4, `(Convert(Property(b, "TestInt16"), Int32) == <param>)` ×2 and its
eleven `BuiltInDataTypes` siblings, `(Convert(c.City, Object) == Convert("London", Object))` ×2.

**INFERRED, and this is the answer to the brief's "is the cast's enclosing expression something A1 itself
handles?":** for the dominant bodies the enclosing expression is a plain comparison or a conjunction of
comparisons — i.e. squarely inside A1's own scope. That is why A1 realizes ~50% rather than A5's 0%. The
remainder of the 140 sits in queries whose *other* clauses are unsupported, which the A/B already prices in.

---

## 8. Question 3, part 2 — which conjunct of the narrowing guard may move

READ + MEASURED. The guard is

```csharp
if (fromType != toType && !(allowNumericWidening && IsWideningNumericConvert(fromType, toType)))
    return null;
```

| conjunct | may it move? | why |
|---|---|---|
| `allowNumericWidening` (the POSITION flag) | **YES** | MEASURED: the driver renders a widening cast explicitly on the comparison path (P01–P04), so admitting it there is driver-identical. Admit it **as an explicit `$toX` node**, not by unwrapping — unwrapping would emit `{$gt: ["$I","$D"]}` where the driver emits `{$gt: [{$toDouble:"$I"}, "$D"]}`; same rows, needless divergence. |
| `IsWideningNumericConvert` (the WIDENING test) | **NO — it may be REPURPOSED, never deleted** | Deleting it and unwrapping is exactly finding 1's defect. It must become a three-way decision: **widening ⇒ unwrap** (order- and value-preserving), **narrowing with a renderable target ⇒ emit `MongoConvertExpression`**, **anything else ⇒ decline**. |
| `fromType != toType` | unchanged | a no-op convert is already correctly unwrapped. |

**Site B's guard (`HasNumericConvert`) gets the symmetric treatment**, and it is the higher-yield edit: tolerate
a widening numeric layer (§5.2's 18), tolerate an enum/char/boxing layer (§5.2's 10), and — for anything else —
either keep `return null` or fall through to `$expr` (§5.2's masked 2). **The `return null` is what makes site B
a hard wall today, and turning it into a fall-through is a separate, independently-gated decision** because it
moves emitted MQL for a currently-working shape.

**The new node kind triggers the stream-1 spike §7 obligation, in a form that document does not spell out.**
`MongoConvertExpression` must be added to `MongoAggregationExpressionRenderer.Render` **and** `CanRender`
(the file's own contract requires them to change together); it must be **left out** of
`MongoQueryLanguageRenderer.IsQueryDialectRenderable` (READ: its `_ => false` default already does this, and
that is CORRECT — a `$toX` has no query-dialect form, and being excluded is what makes it decline cleanly inside
`$elemMatch`, where `$expr` is a hard server error); `MongoExpressionNegator` needs no arm (a convert is never a
boolean, and its `IsQueryDialectRenderable` gate already fails closed); `MongoFieldPrefixRewriter` needs a case
(its default THROWS, READ, so a `SelectMany` inner filter carrying a cast would hard-fail without one); and
**`MongoExpressionTranslator.AllFieldsDefaultSerialized` must recurse through it** — its catch-all returns
`true`, so without a case a value-converted operand hidden under a cast escapes guard B, the identical gap the
slice-B note records for `MongoFilteredSizeExpression`.

---

## 9. Question 5 — what slice B changed for A1

CITED: A1 was measured as carrying **6 slice-B-dependent sole-cause cases**, 2 of them the "ambiguous `Convert`"
cases (stream-1 spike §5.1); slice B's own spike measured the declining `Convert`-rooted sort population as **18
occurrences, all over a query parameter (10) or a transparent identifier (8), not one over a bare mapped
member**.

MEASURED, at `c8a8a5b4` (slice B shipped): **A1's sort column is worth ZERO specification cases.** The sort fix
(§4.4) delivers 0 on both axes; L2's `$toX` node delivers 0; and the census (§7) finds no bare-mapped-member
`Convert` sort key in the declining population, exactly as slice B reported.

**So the 6 slice-B-dependent cases are not unlocked by slice B having shipped.** READ explains why for the
larger half: a `Convert(<x>, Object)` sort key now reaches `TryTranslateValue` via slice B's fall-through, whose
`TranslateOperand` declines a boxing convert (`fromType != toType`, and `IsWideningNumericConvert(int, object)`
is false). A1's §6.1 boxing arm would admit it — but it converts nothing in this suite.

**Consequence for A1's plan: treat the sort column as CORRECTNESS work with zero yield, and plan A1's yield
entirely on the predicate column.** The slice-B multiplier does not apply to A1 the way §7 of the stream-1 spike
implies.

---

## 10. Proposed task split for A1, with a case count per task

Sized against what the prototype actually needed: `git diff --stat` over `src/` reports **109 insertions / 10
deletions across three edited files**, plus two new files (~40 lines) — call it ~150 added lines, and note the
prototype omits the §6.2 conditional constant rule, the `MongoFieldPrefixRewriter` case and the
`AllFieldsDefaultSerialized` case that §8 says a shippable slice needs.
Case counts are this spike's own MEASURED A/B figures, not the CITED 72/56.

| task | content | **cases** | gate |
|---|---|---:|---|
| **0** | *(nothing — this spike is task 0 and it is done.)* | — | this document |
| **1** | **The sort defect (finding 1).** Replace `TryTranslateField`'s blanket `Unwrap` with an order-aware one: strip a widening numeric convert and a boxing convert to `object`; leave any other `Convert` in place so the key declines and slice B's `TryTranslateValue` fall-through handles it. Ship BEFORE any breadth task. | **0** — pure correctness | functional: the S03/S04/S05 fixture, asserting ORDER and comparing against BOTH in-memory LINQ and explicit `DriverLinq`; mutation-verified (restoring the blanket `Unwrap` must turn it red). Spec: zero delta on both axes. |
| **2** | **`MongoConvertExpression`** + `MongoAggregationExpressionRenderer.Render`/`CanRender` + `MongoFieldPrefixRewriter` case + `AllFieldsDefaultSerialized` case; **explicitly NOT** added to `IsQueryDialectRenderable`. `TranslateOperand` admits a cast to `int`/`long`/`double`/`decimal` in both operand positions, declines every other target. | **0** on the spec suite — but it is what task 1 sorts by and task 5 projects | unit tests on both renderer arms; functional: §3.1's P01–P06/P13/P14 under `NativeOnly` with `Native == DriverLinq == CLR` parity; a `$elemMatch` decline test proving the query-dialect exclusion is live |
| **3** | **Site B, the widening relaxation** — `HasNumericConvert` tolerates a widening numeric layer. **Includes the §6.2 constant rule** (comparison-type serialization for a widening cast over a default-serialized property), without which this task ships silent wrong data. | **18** | spec both axes, judged by message transition; functional: the P15/P17 fractional-constant pins (`b,c,e`, never `b,c,d,e`), mutation-verified against the property-serializer form. **No re-baselining needed.** |
| **4** | **Site B, the identity-like arm** — enum → its own underlying type, `char` → `int`, boxing → `object`, keeping the PROPERTY serializer for the constant (§6.2). Re-baseline `NativeGateRoutingTests.A_enum_as_string_where_equals_routing` deliberately, keeping its parity sibling green. | **10** | spec both axes; functional: an enum-as-string fixture asserting VALUES (not just routing) in all three modes |
| **5** | **The projection-leaf gate** — `TryTranslateLeaf` admits a `UnaryExpression{Convert}` leaf, gated on the resulting NODE KIND being `MongoConvertExpression` (the same "it renders as a DOCUMENT, so `$project` cannot misread it as an inclusion flag" argument the count and arithmetic branches already use). | **0** | functional: J01/J02/J04/J07 under `NativeOnly` with parity; a bare-leaf case exercising step 3a's alias/late-fallback route |
| **6** | **The site-B fall-through** (`return null` → fall through to the `$expr` path). Separate task because it moves emitted MQL for a shape that works today, and because it is where the §3.3 CLR-vs-driver divergence decision lands. | **2**, after `EF_TEST_REWRITE_BASELINES` | spec: exactly 2 `AssertMql` re-bases (`Decimal_cast_to_double_works` × async), then `Failed→Passed` on the `NativeOnly` axis; **judged by MESSAGE TRANSITION, never the pass count** |
| **7** | Re-baseline the two `NativeExprComparisonTests` decline tripwires as capability tests; three-EF-version solution green; `BREAKING-CHANGES.md` check. | — | 0 failures on EF8/EF9/EF10 |

**Total: 30 cases (28 without task 6's re-baselining).**

**Sequencing note.** Task 3 alone is 60% of the yield and needs only task 0; task 1 needs task 2 to convert
(rather than merely decline) a narrowing sort key, but is correct and shippable without it. **Do not ship task 3
before its constant rule** — that is the one ordering in this table that is a correctness constraint rather
than a preference.

**Break check (INFERRED, from READ facts).** `MongoQueryMode.cs` does not exist at `v10.0.2`/`v9.1.2`/`v8.4.2`,
so every mode-dependent statement here is vacuous at the published baseline and every touched type is
`internal`. Per the rubric, fallback → native with unchanged results and changed emitted MQL is not a break. The
one thing to re-check at implementation time is task 6's CLR-vs-driver divergence (§3.3), which changes RESULTS
for a currently-falling-back shape — **no owner ruling exists; §11 records it as open.**

---

## 11. What is UNVERIFIED

- **Whether A1 should reproduce the driver's cast-DROPPING on the query-dialect narrowing path, or the CLR's
  answer.** §3.3 MEASURES that the driver returns `a,b,c,e` where the CLR returns `a,b,c` for
  `(int)x.D > 0`, and that a natural implementation produces the CLR answer. **No owner ruling exists**, and
  this spike does not invent one. It is task 6's blocking decision.
- **Out-of-range conversion behaviour.** `$toInt` errors where C# unchecked wraps; `$toInt` of a
  double truncates toward zero (matching C# on every value probed, none at the boundary). No probe crossed
  `int.MaxValue` or fed `$toInt` a NaN/Infinity. The driver has the same behaviour, so `Native == DriverLinq`
  is INFERRED to hold — not measured.
- **The exact conjunct for §6.2's constant rule.** "Numeric widening over a default-serialized property" is
  what the measurements support; whether `HasDefaultKeySerialization` is precisely the right predicate (versus
  "the property's CLR type is not an enum") was not separated by any fixture.
- **Whether the `L3` enum arm is safe for an enum stored as a STRING beyond the one fixture that exercised it.**
  `A_enum_as_string_where_equals_parity` stayed green, so results were correct there; no fixture with a
  value-CONVERTED (rather than represented) enum was built for this arm.
- **The identity of the CITED 72's individual cases.** This spike did not locate them by name; it measured what
  a prototype converts. The 28/30 and the 72/56 are therefore not reconcilable from this document, and the gap
  is characterised (§5.5) rather than itemised.
- **Nothing here was run on EF8 or EF9.** The prototype adds no `#if` and touches only version-agnostic
  expression-tree and BSON code, so identical behaviour is INFERRED, not measured.
- **The sort fix was not exercised against a COMPUTED cast key inside a `ThenBy` chain mixed with field keys**,
  nor against a set-op operand carrying one.
- **No `EF_TEST_REWRITE_BASELINES` pass was run** for task 6's two cases; the `Failed→Passed` landing is
  INFERRED from the measured message transition, exactly as slice B's was.

---

## 12. Method — how to reproduce

Two throwaway worktrees at `c8a8a5b4`; `MONGODB_URI` and `ATLAS_URI` unset.

**Worktree 1 — the prototype + probe harness.** Four env gates compiled in so one build serves every arm:
`MONGODB_EF_A1` ∈ {0,1,2,3,4}, `MONGODB_EF_A1_SORTFIX`, `MONGODB_EF_A1_LEAF`, `MONGODB_EF_A1_FALLTHROUGH`.
Prototype = a new `MongoConvertExpression`, two `MongoAggregationExpressionRenderer` arms, the
`HasNumericConvert` / `TranslateOperand` / `TryTranslateField` / `TryTranslateLeaf` edits of §8, and the §6.2
constant-serialization variant. Probe harness = one functional `[Fact]` iterating 33 shapes (18 predicate, 8 sort, 7 projection) × {in-memory LINQ,
`DriverLinq`, `Native`, `NativeOnly`} and writing a markdown table plus the emitted MQL to
`$MONGODB_EF_A1_PROBE_LOG`.

Fixture (five rows, seeded in this order), designed so five orders are five distinct permutations:

```
 Label   I        L    D      F     M     S
 a      -1        5    1.6    1.5   2.5   3
 b      70000     3    1.4    2.5   1.5   1
 c       5        1    2.5    0.5   0.5   2
 d       2        8   -1.5    3.5   3.5   4
 e    5000        2    0.5    4.5   4.5   5

 raw   I  : a,d,c,e,b        (uint)I : d,c,e,b,a   (a wraps to 4294967295)
 (short)I : a,d,c,b,e        (70000 truncates to 4464, below e's 5000)
 raw   D  : d,e,b,a,c        (int)D  : d,e,a,b,c   (a/b tie at 1, insertion-stable)
```

```bash
# probe harness, any arm
env MONGODB_EF_A1=3 MONGODB_EF_A1_SORTFIX=1 MONGODB_EF_A1_LEAF=1 \
    MONGODB_EF_A1_PROBE_LOG=/tmp/probe.md \
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build \
    --filter "FullyQualifiedName~A1CastProbeTests"

# the spec A/B, from ONE build
for cfg in 0:0:0:0 1:0:0:0 2:1:0:0 3:1:0:0 2:1:1:0 2:1:0:1 3:1:1:1; do
  IFS=: read A S L F <<< "$cfg"
  for n in 1 0; do
    env MONGODB_EF_A1=$A MONGODB_EF_A1_SORTFIX=$S MONGODB_EF_A1_LEAF=$L \
        MONGODB_EF_A1_FALLTHROUGH=$F MONGODB_EF_NATIVE_ONLY=$n \
      dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build \
        --logger "trx;LogFileName=a${A}s${S}l${L}f${F}_n${n}.trx"
  done
done
```

Then compare the `.trx` files by `(testName → outcome, first 140 chars of message)` and report the
**message-transition** classes. §5.2's fall-through row is invisible to a count comparison.

**Worktree 2 — the census**, PRISTINE apart from the hook: `A1Instr` (a buffered append-only logger gated on
`$MONGODB_EF_A1_LOG`), a decline record at `TranslateOperand`'s `Convert` branch and at both
`HasNumericConvert` call sites, and wrappers around `TryTranslate`/`TryTranslateField`/`TryTranslateValue`
logging `(ok, position, declines-in-this-call, cast pair, whether the body contains any Convert, node type,
body text)` whenever the call failed or the body contained a `Convert`. One full EF10 spec run on the default
`Native` axis (**4593 / 0 / 17** — the pristine baseline, confirming the hook perturbs nothing), then classify
by `cut`/`awk` on the pipe-delimited log.
