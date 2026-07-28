# Filtered `Count(pred)` over an owned collection — design (EF-359)

*Branch `EF-359`, stacked on `NativeQueryOngoing` tip `33fdc58` (owned-data slice 8). Ninth owned-data slice of
epic EF-322. Written 2026-07-30.*

---

## 1. What this fixes, and why it is a bug fix rather than a widening

`Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })` — a **filtered** count over an owned
(embedded) collection, appearing as a projection leaf — throws `InvalidOperationException` ("The LINQ expression
'o' could not be translated…") **identically under `Native`, `DriverLinq` and `NativeOnly`**. That is measured,
not inferred: `NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode`
pins it today, and the crash happens inside `MongoProjectionBindingExpressionVisitor.Translate`, reached
unconditionally from `MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` at **translation** time —
before `MongoQueryMode` is read by the compile-time gate. The mode therefore has no bearing on whether it crashes.

This distinguishes EF-359 from the eight owned-data slices before it. Those were **fallback → native** flips: the
shape already worked via driver-LINQ and the slice moved it onto the native pipeline with results unchanged. Here
there is no working path in any mode, so:

- The work is **strictly additive**. There is no prior behaviour to regress for shape A (§2), because there is no
  prior behaviour — every mode produces an exception and no data.
- There is **no driver-LINQ oracle** for the shapes that crash. Correctness has to be proven against an
  independent oracle (§7), not against `Native == DriverLinq`.
- The exception **type** is not contract for an unsupported shape (versioning rubric, `AGENTS.md`), so replacing
  it with working behaviour is not a break, and no `BREAKING-CHANGES.md` entry is required. (Contrast EF-358,
  which needed one because its pre-fix `null` was a usable discriminator that consumers could observe.)

**Root cause.** Both recognition sites are keyed to the **predicate-less** `Count`/`LongCount` overloads:

- `MongoExpressionTranslator.TryMatchCountExpression` matches `MethodCallExpression { Arguments.Count: 1 }` only.
- `MongoProjectionBindingExpressionVisitor.IsCanonicalCountWithoutPredicate` compares against
  `QueryableMethods.CountWithoutPredicate` / `LongCountWithoutPredicate` and the `EnumerableMethods` pair.

So a predicated call is recognized by neither, falls through to the generic
`methodCallExpression.Update(newArguments)` rebuild path in `VisitMethodCall`, and dies there. The two sites must
widen **in lockstep**: widening only the translator leaves the shaper unable to read the leaf back; widening only
the visitor registers a projection member the binder never populated.

---

## 2. Scope — three spellings, three dispositions

| | Shape | Today | After this slice |
|---|---|---|---|
| **A** | `Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })` | `InvalidOperationException`, every mode | **Native**: `$project` leaf `{$size: {$filter: …}}` |
| **B** | `Where(b => b.Posts.Count(p => p.Rank > 0) > 2)` | falls back (to be measured in T0) | **Native**, `$expr` tier only |
| **C** | `Select(b => b.Posts.Count(p => p.Rank > 0))` | crashes | Not native; **correct values**, folded client-side over `aggregate([])` |

**Breadth for A:** `Count(pred)` and `LongCount(pred)`; the anonymous-type spelling *and* the named-DTO
`MemberInit` spelling (a different branch of `NativeProjectionBinder`); a collection reached through one or more
owned single-reference hops (`b.Home.Notes.Count(pred)`); a filtered-count leaf alongside plain-member and
unfiltered-count sibling leaves; arithmetic wrapping the count (`Count(pred) * 2`, which reaches the
pre-existing arithmetic branch with the new node as an operand); several filtered-count leaves in one projection.

**Breadth for B:** all six comparison operators, either operand order, constant **and** parameterized thresholds.
Note there is no Tier-1 (array-index `$exists`) form for a filtered count, so B is `$expr`-only — see §4.

**C is not native, and that is not a count-specific limitation.** A bare (non-`new {…}`) selector body never
populates `Select.Projection` at all — the SP3-wide bare-projection boundary, the same one that keeps
`Select(b => b.Posts.Count)` and `Select(b => b.Posts)` on the fallback path. C is in scope only because it
*crashes* today; the fix makes it fold client-side and return correct values, exactly as the bare **unfiltered**
count has done since owned-data slice 7. Lifting the bare boundary is separate, larger work (§8).

---

## 3. The new IR node — a sibling type, not a flag

```csharp
internal sealed class MongoFilteredSizeExpression : MongoExpression
{
    public MongoFilteredSizeExpression(string arrayPath, MongoExpression elementPredicate, Type type);
    public string ArrayPath { get; }
    public MongoExpression ElementPredicate { get; }   // element-relative by construction
    public override Type Type { get; }
}
```

**This is the load-bearing design decision of the slice, and the reason is silent wrong data.** Four existing
sites match on `is MongoSizeExpression`:

| Site | What it does with a `MongoSizeExpression` | What it must do with a *filtered* one |
|---|---|---|
| `MongoQueryLanguageRenderer.TryRenderSizeComparison` | Tier 1: renders an integer-constant comparison as an array-index existence test, `{"Posts.2": {$exists: true}}` | **Must not fire.** That form answers the question "does the array have more than 2 elements", i.e. the **unfiltered** count — wrong rows, silently |
| `MongoQueryLanguageRenderer.IsQueryDialectRenderable` | Admits a size comparison by calling `TryRenderSizeComparison` | Must decline, so the comparison routes to `$expr` (and so that a filtered count nested inside `$elemMatch` declines rather than emitting illegal MQL — §6) |
| `MongoExpressionNegator` | Inverts the operator (`>` ↔ `<=`) — exact, because the *rendered* `$exists` form partitions the value space | Must decline. The `$expr` form's operators do **not** partition, so inversion is not the exact complement |
| `NativeProjectionBinder.TryTranslateLeaf` | Node-kind gate admitting the leaf | Must admit — the one site that widens |

With a `bool Filtered` flag on the existing node, three of those four rows are wrong **by default** and only
become right if a future editor remembers to add a guard at each. As a separate type every one of them fails
**closed by construction** — a pattern match that names `MongoSizeExpression` simply does not match, and the
default arms already decline. This is the same choice, for the same reason, that owned-data slice 8 made in
preferring a sibling `ArrayAliasProjectionExpression` over a flag on `ObjectArrayProjectionExpression`.

Two consequences worth stating:

- **No `NullSafe` flag.** `MongoSizeExpression` carries one because its unfiltered form is shared with the
  projected reference-collection count, where the array is a `$lookup` output and therefore always present. A
  filtered count has no such analogue, so `$ifNull` is unconditional here.
- **`MongoFieldPrefixRewriter` needs a case**: prefix `ArrayPath` only, pass `ElementPredicate` through
  untouched. This mirrors the existing `MongoElemMatchExpression` case exactly, and for the same reason — the
  element predicate is element-relative by construction, and rewriting it would mis-address every field inside
  the `$filter`.

---

## 4. Rendering

One new arm in `MongoAggregationExpressionRenderer`:

```json
{ "$size": { "$filter": { "input": { "$ifNull": ["$Posts", []] },
                          "as": "e",
                          "cond": { "$gt": ["$$e.Rank", 0] } } } }
```

- **`$ifNull` is mandatory, not defensive.** `$size` (and `$filter`'s `input`) evaluated against a missing or
  explicitly-null array is a hard server error that aborts the whole aggregate command — not a wrong answer.
  Mapping both states to `[]` yields 0, which is what LINQ answers for a missing embedded array.
- **Element field refs must render `$$e.Rank`, not `$Rank`.** `MongoAggregationExpressionRenderer.Render` gains
  an optional `string? elementVariable = null` threaded through its recursion; when set, `MongoFieldExpression`
  and `MongoElementRefExpression` render `"$$" + elementVariable + "." + name`. Default `null` keeps every
  existing call site byte-identical, so no committed MQL baseline moves for any other shape.
- *Rejected alternative:* rewriting the predicate's field paths into `MongoElementRefExpression("$e.Rank")` so
  that the existing `"$" + Path` arm accidentally emits `$$e.Rank`. A node whose `Path` holds a `$`-prefixed
  string misreports its own contents and is a trap for the next reader.
- **Variable naming.** MongoDB constrains a `$filter` `as` name to begin with a lowercase ASCII letter (or a
  non-ASCII character), and nested filtered counts need distinct names per level. Both are T0 spike items (§5).

Tier 1 is **structurally unreachable** for this node (§3), so shape B renders only through `$expr`:

```json
{ "$expr": { "$gt": [ { "$size": { "$filter": { … } } }, 2 ] } }
```

**Index expectation, stated as an expectation rather than a claim.** The unfiltered `.Count` slice measured its
own array-index form as a COLLSCAN, contrary to that design's assumption. A `$size`-over-`$filter` inside
`$expr` is expected to be a COLLSCAN too, and this slice does not claim otherwise — T0 records an explain plan so
the expectation is measured rather than assumed, in either direction.

---

## 5. Recognition and gates — four edits in lockstep

1. **`MongoExpressionTranslator.TryMatchCountExpression`** — additionally match the 2-argument
   `Count`/`LongCount`, by reference-equality against the canonical `WithPredicate` `MethodInfo` definitions
   (`QueryableMethods` and the provider's `EnumerableMethods` port), comparing generic *definitions* since an
   open definition and a constructed instantiation are never reference-equal. Report the lambda to the caller.
   The existing `node.Type is int or long` pre-filter and the `MemberExpression`/`.Count`-property arm are
   untouched.

2. **`MongoExpressionTranslator.TranslateOperand`'s count branch** — when a predicate is present:
   1. Run `ReferencesEnclosingScope` on the lambda body **before** constructing anything (the same guard, in the
      same position, as the quantifier arm), so a correlated element predicate declines.
   2. Translate the body with a **fresh element-scoped** `MongoExpressionTranslator(elementType)` — the same
      construction the quantifier arm uses, which is what makes the resulting field paths element-relative.
   3. Require `MongoAggregationExpressionRenderer.CanRender` on the translated predicate (§6).
   4. Emit `MongoFilteredSizeExpression`.

   The predicate-less path through this branch is unchanged, so every shape the `.Count` predicate and count-
   projection slices made native keeps its exact emitted MQL.

3. **`NativeProjectionBinder.TryTranslateLeaf`** — the node-kind gate widens to
   `is MongoSizeExpression or MongoFilteredSizeExpression`. It stays a **node-kind** gate, never "translation
   succeeded": that narrowing is what keeps a bare constant/parameter leaf out, and a `0`/`false` constant leaf
   aborts the aggregate (`Cannot do exclusion on field X in inclusion projection`) — measured by slice 7.

4. **`MongoProjectionBindingExpressionVisitor`** — two edits:
   - `IsCanonicalCountWithoutPredicate` extends to the predicated canonical definitions (and is renamed
     accordingly). The `Route == NativeRoute.Projection` guard is retained unchanged: it is what keeps a mixed or
     fallback shape falling through untouched, and what makes this block and the `Queryable` switch's own
     `Count` arm disjoint by construction.
   - The EF-357 arm in the `Queryable` switch — which rebuilds `Queryable.Count`/`LongCount` over a
     `CollectionShaperExpression` against the `Enumerable` equivalent — widens to the predicated overloads
     (`CountWithPredicate`/`LongCountWithPredicate`). **This is what delivers shape C**, and only shape C: a bare
     selector body leaves `Route != Projection`, so edit 4a does not fire and the rebuilt call folds
     client-side over `aggregate([])`. The decline path stays `break`, not `return null` — `return null` folds
     through `MatchTypes(null, typeof(int))` to `Expression.Default(int)` and silently returns 0.

---

## 6. The new classifier, and everything that declines

**`MongoAggregationExpressionRenderer.CanRender(MongoExpression)`** — new, mirroring `Render` arm-for-arm:
field refs, element refs, constants/parameters, `MongoBinaryExpression` (comparisons, `&&`/`||`, arithmetic),
`MongoSizeExpression`, and the new `MongoFilteredSizeExpression`; everything else declines. It joins the
existing "these must change together" contract that already binds `IsQueryDialectRenderable` ↔ `RenderNode` ↔
`MongoExpressionNegator`, and it is reusable by any later aggregation-dialect work.

**Why it must exist rather than letting `Render` throw.** Without it, an element predicate the renderer cannot
express (regex from `StartsWith`/`EndsWith`/`Contains`, `$in` from `Contains`, `Not`, a nullable bare bool) would
be accepted at translate time and throw `NativeTranslationNotSupportedException` at *render* time. Under `Native`
the gate catches that and falls back — onto the pre-existing crash. So the shape would fail with a *different*
exception from a *different* place than it does today. Declining at translate time keeps it failing
byte-identically, which is the disposition this slice is obliged to preserve for anything it does not fix.

**Declines, each to be pinned by a test:**

| Shape | Mechanism | Disposition |
|---|---|---|
| Correlated element predicate — `Count(p => p.Rank > b.Threshold)` | `ReferencesEnclosingScope` | Decline. See the note below |
| Element predicate outside `CanRender` — regex, `Contains`, `Not`, nullable bool | `CanRender` | Decline at translate time; failure byte-identical to today |
| Two-scope (`SelectMany`) filtered count | `TryResolveOwnedCollectionPath` declines outright in two-scope mode | Decline |
| Primitive-element collection — `b.Tags.Count(t => …)` | Resolver's final-hop-is-a-collection-**navigation** check | Decline |
| Reference (non-owned) collection filtered count | Resolver is embedded-only; `Filtered_reference_collection_count_still_falls_back` stays as-is | Unchanged |
| `!(b.Posts.Count(pred) > 2)` | `MongoExpressionNegator` has no arm for the new node | Graceful fallback |
| A filtered count nested inside a quantifier — `b.Posts.Any(p => p.Comments.Count(c => …) > 1)` | `IsQueryDialectRenderable`'s size arm does not match the new node | **Must decline, and cleanly** |

**The nested-in-quantifier row is the one to watch.** `$expr` inside `$elemMatch` is a hard server error, so a
filtered count there must never be admitted — if it were, the whole query would throw at *execution* time under
default `Native`, not merely fail to use an index. The decline falls out for free from the new node not matching
`IsQueryDialectRenderable`'s size arm, but "declines gracefully rather than throwing at render time" is a T0
assertion, not an assumption: `RenderNode`'s behaviour for an unmatched node in that position must be checked.
Note the *unfiltered* `.Count` inside a quantifier stays native, via Tier 1 — that must not regress.

**On the correlated decline — a capability difference from `$elemMatch` worth recording.** `$elemMatch` cannot
reference the enclosing document *at all*, which is why the quantifier slices' correlated decline is a hard
architectural limit. A `$filter` `cond` **can**: `{$gt: ["$$e.Rank", "$Threshold"]}` is legal. So a correlated
filtered count is a deferrable *capability* here (it needs a two-scope element translator), not an impossibility.
It is still declined by this slice, and the decline is still load-bearing for the documented reason: single-scope
`TryResolveMember` resolves members **by name** with no parameter-identity check, so an enclosing access whose
name also exists on the element would be silently retargeted at the element — wrong numbers, not a decline.

---

## 7. Verification

**T0 spike, GO/NO-GO, before any implementation.** Findings can change §4 and §6.

1. Reproduce the current failure for A, B and C in all three modes; record exception type, message and origin for
   each. In particular establish B's *actual* current disposition — the status doc records it as never
   re-measured — and whether C's crash is the same one as A's.
2. Against a live server: confirm `$size` over `$filter` with `$ifNull` returns 0 for a missing array, 0 for an
   explicit BSON null, 0 for `[]`, and the right count otherwise. Confirm the accepted `as` variable-name rules
   and that a **nested** `$filter` can reference the outer variable with distinct names.
3. Confirm what `MongoQueryLanguageRenderer.RenderNode` does with an unmatched node in the `$elemMatch` element
   position — a clean decline upstream, or a throw. §6's nested-in-quantifier row depends on the answer.
4. Record a `queryPlanner` explain for shape B (expected COLLSCAN; measured either way).
5. Confirm whether a driver-LINQ oracle exists for **any** of the three shapes, so §7's test strategy per shape
   is measured rather than assumed.

**The primary bar is a differential-matrix oracle test**, following the `All` slice's precedent, because a
filtered count can return a silently wrong **number** — this is not a shape where an error is the likely failure
mode. The **same `Expression` object** is sent to the server and compiled for in-memory evaluation, over a seed
covering array states (multi / single / empty / missing / explicit BSON null) × element states (predicate
matches / does not match / predicate's field missing / predicate's field explicitly null). Driver-LINQ is not
usable as the oracle here: for A and C it crashes, and for a wrapped count it renders a bare `$size` with no
`$ifNull` and aborts on ragged data (measured by slice 7).

**Mutation-verified tripwires** — each must be shown to go red when the thing it protects is removed, per the
lesson that three tests in an earlier slice passed with the guard they tested deleted:

- The node-kind gate in `TryTranslateLeaf` (widen it to plain `TryTranslateValue` success → red).
- The `CanRender` decline (delete the check → the shape fails differently → red).
- **The Tier-1 fail-closed property specifically**: a test asserting the emitted MQL for shape B contains
  `$filter` and does **not** contain an array-index `$exists` form, which goes red if the new node is ever
  collapsed into `MongoSizeExpression`. This is the test that guards the §3 decision, so it must exist and be
  proven non-vacuous.
- The unfiltered `.Count`-inside-a-quantifier path staying native (a regression tripwire, not a new capability).

**Sweeps:** three-version `/test-all` (EF8 / EF9 / EF10), 0 failures required on all three; and the EF10 spec
suite on **both** axes (`Native` MQL baselines *and* the `NativeOnly` pass/fail set) — per the lesson that
inventorying only the `NativeOnly` pass set missed a flip in the `All` slice. Northwind has no owned collections,
so zero delta is the expectation on both axes; a non-zero delta is a finding, not a re-baseline.

---

## 8. Out of scope

- **Correlated element predicates** — deferrable capability, needs a two-scope element translator (§6).
- **Widening the aggregation renderer** for `$not` / `$in` / `$regexMatch`. Those arms are shared with every
  other `$expr` site, so the blast radius is much wider than this slice; and `$expr`-dialect regex needs
  `$regexMatch`, a different operator from the query dialect's `$regularExpression`, so it is new rendering
  rather than reuse.
- **The bare form going native** — the SP3-wide bare-projection boundary, unchanged (§2).
- **Reference-collection filtered count** — `NativeCorrelationMatcher`'s exactly-one-correlation guard rejects it
  today and continues to.
- **The interposed-operator family** (`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat` between an
  owned-collection `Select` and a terminal operator — duplicate-key `_collectionShaperMapping.Add`), recorded on
  the EF-322 epic. Same fall-through root cause as EF-357/EF-359, different fix.
- **The general `MatchTypes` fix** — that `Queryable` overloads other than `Count`/`LongCount` (`First`, `Any`,
  `Sum`, …) are never rebuilt against their `Enumerable` equivalents remains an untaken follow-on, as it was
  after slice 7. This slice widens the same two overloads it already covered, nothing more.

---

## 9. Multi-version, and not a break

No `#if` expected — every touched type is `internal` and nothing here is EF-version-conditional. Per the
versioning rubric in `AGENTS.md` this is not a breaking change on any count: a shape that produced no data in any
mode now produces data (shapes A and C); a fallback → native routing flip with unchanged results (shape B); the
emitted MQL for a supported query is not contract; and the exception type of a still-unsupported shape is not
contract. **No `BREAKING-CHANGES.md` entry** — do not add one by analogy with EF-358, which needed one because
its pre-fix `null` was an observable discriminator, whereas nothing observable changes value here.

---

## 10. Risk profile

The eight slices before this one were fallback → native flips whose results were structurally unchanged. This
one is different in both directions, and the difference cuts both ways:

- **Lower risk than slice 8** in the one way that matters most: shape A currently produces *no data in any mode*,
  so there is no working behaviour to silently break, and none of the alias-addressing hazards that produced two
  live silent-wrong-data bugs in slice 8 apply — this leaf reads back as an ordinary scalar by alias, exactly
  like the unfiltered count leaf.
- **Higher risk in one specific place**: the filtered count shares a node *position* with the unfiltered count,
  whose Tier-1 rendering answers a **different question**. A representation that let the two be confused would
  return wrong numbers silently, under default `Native`, on ordinary data. §3 is designed to make that
  structurally impossible rather than guarded against, and §7's Tier-1 tripwire is the test that proves it.

Shape B is the one shape here with a working pre-existing path (fallback), so it is the one where a
fallback → native flip could change results. Its differential-oracle rows are therefore not optional.

---

## 11. As-built deltas

*Added at Task 6 (2026-07-30), after the implementation landed. **§§1–10 above are left exactly as written** —
they are the dated design record, including the claims measurement later refuted. This section is the correction
layer: every place the implementation diverged from the design, and every claim the work measured false. A reader
who reads §§1–10 alone will be misled on at least six points: the first four below, plus the two in §11.9, which
was added at the whole-branch review after §§11.1–11.8 were written.*

### 11.1 §6's justification for `CanRender` is MEASURED FALSE — and the guard was kept anyway, on scope grounds

§6 says: without `CanRender`, an element predicate the renderer cannot express "would be accepted at translate
time and throw `NativeTranslationNotSupportedException` at *render* time. Under `Native` the gate catches that and
falls back — onto the pre-existing crash."

**Measured, A/B, on `Select(b => new { N = b.Posts.Count(p => p.Heading.StartsWith("h")) })`:**

| | `Native` | `DriverLinq` | `NativeOnly` |
|---|---|---|---|
| **With** the `CanRender` check (as shipped) | `InvalidOperationException` | `InvalidOperationException` | `InvalidOperationException` |
| **Without** it | correct value | correct value | clean decline |

So the fallback does **not** land on the pre-existing crash: it *works*. The check **preserves a crash that its
removal would turn into a working fallback**. Two further corrections to §6's framing: `CanRender` has **no
correctness role** — the `$expr`-inside-`$elemMatch` hazard §6 worries about is `IsQueryDialectRenderable`'s job,
not `CanRender`'s — and it is materially observable only on the **projection** path (in the predicate position
both routes end in a graceful driver-LINQ fallback either way).

**Disposition: the guard ships anyway**, by owner ruling, on scope grounds — deleting a guard in order to widen
admissibility is exactly the direction that produced two live silent-wrong-data bugs in owned-data slice 8, and
EF-359's remit is the renderable cases. The improvement is filed as **EF-365**, which also records the breadth
still unverified: only `StartsWith` was measured. `Contains`/`$in`, unary `Not`, a bare nullable bool, and a
**mixed** projection (a declining leaf beside an admitted one, where the driver may not emit the alias the shaper
reads) are all untested.

This also revises §7's mutation-tripwire list, which expected "delete the `CanRender` check → the shape fails
differently → red". It does go red, but not for the reason predicted: the shape stops failing altogether.

### 11.2 The plan prescribed DECLINING a null-comparison element predicate; the owner OVERRODE that

The implementation plan's Task 5 step 3 said, of a `null_check` divergence: *"the honest fix is to make
`CanRender` (or the translator) **decline** a null-comparison element predicate and pin the decline, not to weaken
the oracle."*

The divergence was measured and is real. **The owner overrode the plan: accept and document — no rendering
change, no null-guard, no decline.** The reasons, recorded so the override is not re-litigated as an oversight:

- **Native and `DriverLinq` agree with each other** on every ragged row; only in-memory LINQ differs. Declining
  would move the shape off native onto a path that gives the *same* "divergent" answer — buying nothing.
- The divergence is not specific to `== null`. It is the same one BSON ordering produces for relational
  operators, which §6 never proposed declining, and which no earlier slice declined either.
- Declining an ordinary, common predicate shape to hide a dialect property is a larger behavioural claim than
  documenting it.

Measured numbers, over a seed of `{Rank: null}` / `{Rank absent}` / `{Rank: 1}`:

| Element predicate | in-memory LINQ | native | `DriverLinq` |
|---|---|---|---|
| `Count(p => p.Rank < 5)` | 1 | 3 | 3 |
| `Count(p => p.Rank > 4 \|\| p.Rank < -4)` | 2 | 4 | 4 |
| `Count(p => p.Rank == null)` | 2 | 1 | 1 |

### 11.3 The missing-vs-null MECHANISM was misdescribed once, and the corrected version is the one to carry

A fix-round draft explained the `== null` row as *"a divide within the dialect's own equality operator"* — i.e.
that MQL's `$eq` was inconsistent with MQL's relational operators. **That is refuted.** An independent
`$unwind` + `$project` probe against an absent field measured `$type` = `"missing"`, `$cmp: [field, null]` = `-1`,
`$eq: [field, null]` = `false`, `$lt: [field, 0]` = `true`; and `$cmp` = `0` for an explicit null.

So there is **one consistent BSON total order, `missing < null < numbers`**, used by `$eq` and the relational
operators alike: `$eq` is false because `$cmp != 0`, and `$lt` is true for exactly the same reason. **The actual
gap is on the CLR side** — the CLR collapses two distinct BSON values (missing and explicit null) into a single
`null`, so *any* comparison able to distinguish them disagrees with LINQ.

Two corollaries, both measured:

- The `||` row is **not a separate mechanism**. It is the relational divergence reached through a disjunct, where
  the `< -4` operand matches both ragged elements with nothing masking it. Contrast `p.Rank > 0 && p.Rank < 6`,
  which *agrees* with LINQ: the `> 0` conjunct already resolves both ragged elements to false regardless of what
  `< 6` alone would answer. Do not generalize "any predicate containing `<` diverges".
- `Count(p => p.Rank + 1 > 0)` does **not** diverge, because `$add` collapses missing to null before the
  comparison runs.

### 11.4 §2's table said shape B's current disposition was "to be measured" — it was, and it already worked

§2 recorded shape B (`Where(b => b.Posts.Count(pred) > 2)`) as "falls back (to be measured in T0)". Task 0
measured it: it **already fell back and returned correct results** in every mode. It is therefore the one shape in
this slice that is an ordinary fallback → native flip, exactly as §10 anticipated, and the only one where the flip
could have changed results. Nothing about the design changed as a result — but a reader of §2 alone cannot tell
whether B crashed like A and C, and it did not.

### 11.5 An unplanned incidental widening: arithmetic over a WRAPPED filtered count went native for free

*There turned out to be **two** unplanned widenings, not one — the second (a filtered count inside an owned
`SelectMany`'s inner filter) was found later, at the whole-branch review, and is recorded in §11.9.1.*

Not in the design at any point. `NativeProjectionBinder.TryTranslateLeaf`'s pre-existing EF-347 arithmetic branch
gates on "a `BinaryExpression` arithmetic top node plus `TryTranslateValue` succeeded", with no restriction on
which operand kinds produce that success. So the moment §5 edit 2 taught `TranslateOperand` to recognize a
predicated count as an ordinary operand, `Select(b => new { X = b.Posts.Count(pred) * 2 })` became native too —
discovered in Task 2's fix round, not at design time, and pinned by
`Arithmetic_projection_leaf_containing_a_filtered_count_goes_native` (which verifies `Native`/`NativeOnly`/
`DriverLinq` parity across the ragged seed, including a missing/null array).

The **bare** analogue (`Select(b => b.Posts.Count(pred) * 2)`) is *not* native and still hard-fails: the count
call is an operand of the top-level `*` rather than the selector body, so the identity guard in 11.6 declines it.

### 11.6 §5 edit 4b needed TWO guards, not one — and neither is redundant

§5 edit 4b (widen the EF-357 `Queryable.Count` rebuild arm to the predicated overloads, to deliver shape C)
described no additional guard. Two were required, each load-bearing for a **different** residual — established by
measurement in fix rounds, not by reasoning:

- **Top-level-expression identity** (`ReferenceEquals(methodCallExpression, _translatedRootExpression)`) holds the
  **wrapped non-renderable** residual (the `StartsWith` case). That predicate references only its own element
  parameter — no shaper node, no query parameter — so the structural check below would let it through.
- **`ContainsShaperReference`** (declining when the predicate body contains a `StructuralTypeShaperExpression` /
  `ProjectionBindingExpression` / `EntityProjectionExpression`) holds the **bare correlated** shape
  (`Select(b => b.Posts.Count(p => p.Title == b.Title))`). There the count call *is* the root, so identity holds
  and the arm would proceed. `ReplacingExpressionVisitor` has already substituted the outer parameter with the
  query root's entity shaper by the time this visitor runs, and the predicate is deliberately not re-visited, so
  without this guard the shape degraded to a **worse** failure than the pre-existing one:
  `KeyNotFoundException("…'EmptyProjectionMember'…")` at shaper-compile time instead of the clean
  `InvalidOperationException`.

A third narrowing was also needed, likewise unanticipated: a **captured local** in the element predicate arrives
as an EF query-parameter node that the client-side fold cannot evaluate (measured: `ArgumentException: must be
reducible node` from the lambda compiler). A `ContainsQueryParameter` guard declines it, so that one spelling
keeps failing with the same pre-existing `InvalidOperationException` as every other declined shape rather than a
confusing new one. **Consequence for §2's shape C:** shape C ships narrower than designed — constant-bodied
element predicates fold client-side and return correct values; a captured-local one still hard-fails in every
mode, as it did before.

### 11.7 Declines: broader and narrower than §6's table in specific places

- **§6 row "Element predicate outside `CanRender`" — its disposition splits by position.** In the *predicate*
  position it is a graceful fallback with correct results (`Element_predicate_outside_the_renderable_set_declines`).
  In the *projection* position it is a preserved hard crash in all three modes
  (`Non_renderable_element_predicate_filtered_projection_still_hard_fails_in_every_mode`) — §6's "failure
  byte-identical to today" is true, but "today" there means a crash, not a decline. See 11.1 / EF-365.
- **§6 row "Two-scope (`SelectMany`) filtered count → declines outright" is MEASURED FALSE for the OWNED
  inner-filter path — see §11.9.1.** This bullet originally read: "The decline is genuinely inherited —
  `TryResolveOwnedCollectionPath` declines outright in two-scope mode — and that guard is pinned by the
  quantifier-era `Two_scope_owned_collection_Any_is_declined`. No new test was added for the count spelling; the
  row is admissibility-by-inheritance, not independently verified." The "not independently verified" caveat was
  the right instinct and the verification came back negative: the owned `SelectMany` inner filter does not use a
  two-scope translator at all, so the inherited decline never fires and the shape goes **native**. The
  `TryResolveOwnedCollectionPath` two-scope guard is real and still declines a genuinely two-scope (outer-
  referencing) filter; it is simply not what this row's shape reaches.
- **A residual family §6 did not enumerate at all**, found by branch review and now pinned: the
  `Posts.Where(pred).Count()` spelling hard-fails in every mode for a *structurally distinct* reason — EF Core
  does **not** fuse it into `Count(pred)` upstream of this provider (measured directly), so it arrives as
  `CountWithoutPredicate`, the pre-existing EF-357 arm, untouched by this slice.
- **§6's correlated row is correct but its "deferrable capability" note is worth restating as a contrast**, since
  it inverts the quantifier slices' rule: `$elemMatch` cannot reference the enclosing document at all, but a
  `$filter` `cond` **can** (`{$gt: ["$$e.Rank", "$Threshold"]}` is legal), so this decline needs only a two-scope
  element translator. It ships declined for the reason §6 gives (single-scope `TryResolveMember` resolves by name
  with no parameter-identity check, so an enclosing access sharing a name with an element member would be
  silently retargeted — wrong numbers, not a decline).

### 11.8 Confirmed exactly as designed (recorded so the next reader does not re-measure)

- **`$ifNull` is mandatory, and the failure is a hard server error.** Task 0: without it, a missing or
  explicitly-null array aborts the aggregate with `The argument to $size must be an array, but was of type: null`.
- **The `as` variable-name rule.** Task 0 measured `"e"` and `"ee"` accepted; `"_e"` and `"E"` both **rejected**
  (`'…' starts with an invalid character for a user variable name`). So the name must begin with a lowercase ASCII
  letter, and the nested derivation `outer + "e"` is safe by construction.
- **Shape B's index behaviour.** §4 stated a COLLSCAN *expectation* rather than a claim; Task 0's `queryPlanner`
  explain measured `winningPlan.stage = "COLLSCAN"`, `rejectedPlans: []`. Expectation confirmed, in the direction
  predicted.
- **The sibling-not-a-flag decision (§3) paid off twice**, on both of the two call sites that widened (Task 1's
  translator and Task 3's projection binder), and the Tier-1 fail-closed property is pinned by an MQL assertion
  that the emitted pipeline contains `$filter` and no array-index `$exists` form.
- **§9's multi-version claim.** No `#if` was added or removed anywhere in `src/`; the three-version sweep
  (`Debug EF8` / `Debug EF9` / `Debug EF10`) reported 0 failures on each.
- **§9's not-a-break claim.** No `BREAKING-CHANGES.md` entry was added. The EF10 specification suite was run on
  both axes (default `Native` for the MQL baselines and `MONGODB_EF_NATIVE_ONLY=1` for the pass/fail set) against
  the branch base `33fdc58`: **zero delta on both axes, pass-set and fail-set set-identical**, as §7 predicted
  (Northwind has no owned collections).

### 11.9 Added at the whole-branch review (after §§11.1–11.8 were written)

#### 11.9.1 A THIRD incidental widening, and §6's two-scope decline row is measured false for it

Not in the design, and it contradicts §6's decline table. A filtered count inside an **owned `SelectMany`'s inner
filter** now goes native:

```csharp
SelectMany(b => b.Posts.Where(p => p.Comments.Count(c => c.Age > 0) > 1), (b, p) => new { p.Heading })
```

emits, after `$unwind: "$Posts"`, a top-level `$match` of
`{$expr: {$gt: [{$size: {$filter: {input: {$ifNull: ["$Posts.Comments", []]}, as: "e", cond: {$gt: ["$$e.Age", 0]}}}}, 1]}}`
— legal because that `$match` is top-level rather than inside `$elemMatch`, and correctly addressed because
`Posts.Comments` names the unwound element's own array.

**Why §6's "two-scope (`SelectMany`) filtered count → declines outright" row does not hold here:**
`NativeSelectManyBinder.TryBuildOwnedInnerFilter` translates an inner-**only** owned filter with a
**single-scope** element-scoped `MongoExpressionTranslator`, not the two-scope one §6 assumed. So
`TryResolveOwnedCollectionPath`'s two-scope decline never fires on this path, and once §5 edit 2 taught
`TranslateOperand` to recognize a predicated count as an ordinary operand, the shape reached native for free. §6's
row remains correct for a genuinely two-scope (outer-referencing) filter, which still routes to the two-scope
translator and still declines.

**Measured before and after, not inferred.** At the branch base `33fdc58`, in a throwaway worktree, a probe over
this exact shape threw `InvalidOperationException` containing "could not be translated" under **all three** modes
(`Native`, `DriverLinq`, `NativeOnly`). So this is a **hard-fail → native fix**, not the fallback → native flip
the review that found it assumed — and there was no driver-LINQ oracle for it then or now, so the pinned row
assertion is the oracle. Pinned by
`NativeOwnedCollectionFilteredCountTests.Filtered_count_inside_an_owned_SelectMany_inner_filter_goes_native`, the
filtered analogue of the pre-existing `NativeOwnedCollectionCountTests.Count_inside_an_owned_SelectMany_inner_filter_goes_native`
(which exists because the `MongoFieldPrefixRewriter` case is load-bearing — the count's array path is
element-relative, `"Comments"`, and `Rewrite` must prefix it to `"Posts.Comments"`; the filtered node goes through
that same rewriter case). The test's threshold is chosen so the `$filter` is load-bearing in the assertion: `"mid"`
has 2 comments but only 1 with `Age > 0`, so a rendering that dropped the filter would return an extra row.

#### 11.9.2 §6's `!(b.Posts.Count(pred) > 2)` row attributes the decline to the wrong mechanism

§6's decline table says the mechanism is "`MongoExpressionNegator` has no arm for the new node". **The negator is
never called for this shape.** `MongoExpressionTranslator`'s `Not` arm gates on
`operand is MongoBinaryExpression { Left: MongoSizeExpression }`, which a `MongoFilteredSizeExpression` fails, so
control never reaches `MongoExpressionNegator.TryNegate`. The node falls through to
`new MongoUnaryExpression(Not, operand)`, and the decline happens at **render** time — in
`MongoQueryLanguageRenderer.RenderUnary`'s *"only supports Not over a MongoFieldExpression or a query-native
comparison"* throw (the operand is a `MongoBinaryExpression` whose `Left` is not a `MongoFieldExpression`, so it is
not query-native) — which `MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline`'s typed
`catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)` converts into a
driver-LINQ fallback.

The **outcome** §6 documents is right (graceful fallback under `Native`/`DriverLinq`, throws under `NativeOnly`),
and the negator does independently fail closed via its own `IsQueryDialectRenderable` gate — but that is a second,
**unreached** line of defence. The consequence for a future editor: **adding a negator arm for the new node would
change nothing here.** The row was also the only entry on the corresponding `AGENTS.md` list with no named test;
it is now pinned by `Negated_filtered_count_comparison_declines_and_falls_back_to_correct_rows` (which seeds the
well-formed subset deliberately — the driver-LINQ fallback renders a bare `$size` with no `$ifNull` and would
abort on the missing/explicitly-null rows for an unrelated reason).

#### 11.9.3 §3's "four sites fail closed" is off by one

§3 argues the sibling-type decision by listing four `is MongoSizeExpression` sites. The count is right; the
conclusion as written in the shipped `AGENTS.md` note ("all four fail closed by construction") was off by one, and
is corrected there in place. Precisely: **three** of the four fail closed by construction (Tier-1 rendering, the
query-dialect classifier, the negator), while the **fourth**, `NativeProjectionBinder.TryTranslateLeaf`, is the one
deliberately **opened**, by naming the new type in its own gate. The property that matters is that fail-closed is
the default and opening a site is a visible edit — not that every site declines.
