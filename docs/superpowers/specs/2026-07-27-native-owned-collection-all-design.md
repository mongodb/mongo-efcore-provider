# Native owned-collection `All` predicates + the shared predicate negator (EF-335) — design

*Epic EF-322 (native LINQ query provider). Owned-data translator slice, following the owned-collection `Any`
(`$elemMatch`) slice.*
*Branch `EF-322-owned-collection-all-native`, stacked on the native tip `791037b`
(`origin/NativeQueryOngoing`).*
*Closes the existing ticket **EF-335** ("`All` with a comparison predicate — negating a comparison into
query-native form is unsupported"). A JIRA number should be filed for the owned-collection half; this doc will
be updated with it.*

---

## 1. Problem

Two shapes, one missing capability.

**(a) An owned-collection universal quantifier falls back.** `ctx.Blogs.Where(b => b.Posts.All(p => p.Rank > 5))`
falls back to driver-LINQ even under `Native` mode. The preceding slice (`791037b`) made owned-collection `Any`
native via `$elemMatch` and named `All` as its first explicit deferral
(`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, the owned-collection `Any` note):

> **Deferred/still falls back:** `All` (`Posts.All(p => ...)`) […]

and, in that slice's own design doc (`2026-07-27-native-owned-collection-predicates-design.md`, §7):

> **`All(pred)`** → `{ path: { $not: { $elemMatch: <negated pred> } } }`. Correct in principle […] but it
> requires **negating an arbitrary element predicate**, and the renderer already has a known
> `Not`-over-unsupported-subtree gap. A mis-negated predicate returns wrong rows rather than declining —
> highest silent-wrong-data risk of the candidate shapes, so it gets its own slice.

**(b) The top-level `All` aggregate has the identical gap — this is EF-335.**
`NativeCardinalityBinder.TryBindAggregate` (`NativeCardinalityBinder.cs:131-143`) negates at the **LINQ**
level:

```csharp
var negated = Expression.Not(predicate.Body);
if (!translator.TryTranslate(negated, out var negatedNode))
    return false;
select.AddPredicateConjunct(negatedNode);
```

`TryTranslate` **succeeds** for a comparison operand — `MongoExpressionTranslator.TranslateNode`'s `Not` arm
(`MongoExpressionTranslator.cs:243-267`) falls through to `new MongoUnaryExpression(Not, operand)` for anything
that is not an `In`/`Regex`/`ElemMatch`/bare-field operand — so the conjunct is accepted, and the failure
surfaces later, at **render** time: `MongoQueryLanguageRenderer.RenderUnary` (`:136-151`) throws
`NativeTranslationNotSupportedException` for a `Not` whose operand is not a `MongoFieldExpression`. The gate
catches that during lower/render and falls back (throwing under `NativeOnly`). So `All(x => x.Rank > 0)` is
native-capable in every respect except that nothing can render the negation.

**The missing capability, shared by both:** a way to produce an **exact complement** of a translated predicate.
Today the only negations the provider can express are the four special-cased `Negated` flips
(`$in`→`$nin`, regex, `$elemMatch`, and `Not` over a bare bool field). There is no general negation, and
`RenderUnary` supports `Not` over a bare field only.

Both (a) and (b) are clean declines today — no wrong `$match` is emitted, and both shapes return correct
results via driver-LINQ.

## 2. Goal & success criteria

**Goal.** Add a shared, exact-complement predicate negator, and use it to make (a) owned-collection `All`
quantifiers and (b) the top-level `All` aggregate go native.

**In scope.**
- `b.Posts.All(p => <element predicate>)` → `{ "<arrayPath>": { $not: { $elemMatch: <¬element predicate> } } }`
- `!b.Posts.All(...)` → `{ "<arrayPath>": { $elemMatch: <¬element predicate> } }` (the existing `Negated` flip)
- Nested quantifiers in either order — `All`-within-`Any`, `Any`-within-`All`, `All`-within-`All`
- The collection may be reached through owned single-reference hops (`b.Home.Notes.All(...)`)
- The element predicate may use the same query-dialect operand set the `Any` slice supports: equality, `!=`,
  relational, `== null`/`!= null`, `&&`/`||`, bare bool, `Contains` (`$in`), `StartsWith`/`EndsWith`/`Contains`
  (`$regularExpression`), and a nested quantifier
- **EF-335:** the top-level `All` aggregate (`ctx.Blogs.All(b => b.Rank > 0)`), including `&&`/`||` predicates

**Out of scope** — see §7. Notably `.Count` in a predicate, embedded-collection projections, and a correlated
element predicate.

**Success bar.**
- The shapes go native (succeed under `MongoQueryMode.NativeOnly`).
- **For every supported element predicate and every element/array state, the server result equals an
  in-memory LINQ oracle over the same seed** (§9 — this, not driver parity, is the primary bar; see §6 for why).
- `Native == DriverLinq` on the well-formed seed, where the driver has a usable oracle.
- `All` over an **empty**, **missing**, and **explicit-null** array is `true`, matching LINQ.
- No-tracking **and** tracked both correct.
- The top-level `All` shapes that are **already** native today (regex, `$in`, bare bool) are provably unchanged.
- Zero regressions across EF8 / EF9 / EF10.

**This changes the native eligibility set** — see §6.

## 3. Approach

### 3.1 The negation strategy

**A — a structural exact-complement negator over the translated `MongoExpression` tree (chosen).** A new
`internal static class MongoExpressionNegator` with a single entry point,
`TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)`. It is pure: no entity-type
knowledge, no scope knowledge, no state. Its contract is **exact set complement, or decline** — it never
approximates.

| input node | complement | why it is exact |
|---|---|---|
| `MongoBinaryExpression{AndAlso}(L,R)` | `OrElse(¬L, ¬R)` | De Morgan; recurses and propagates a child decline |
| `MongoBinaryExpression{OrElse}(L,R)` | `AndAlso(¬L, ¬R)` | De Morgan |
| **query-native** comparison with `Equal` / `NotEqual` | the **mirrored operator** (`NotEqual` / `Equal`) | `$eq` and `$ne` partition every BSON value, **including missing and null**, so inversion is exact here. Rendered by the existing `RenderComparison`. |
| **query-native** comparison with `LessThan` / `LessThanOrEqual` / `GreaterThan` / `GreaterThanOrEqual` | `MongoUnaryExpression(Not, cmp)` | relational operators do **not** partition (see approach C) — these are `$not`-wrapped, never inverted |
| a comparison that is **not** query-native (field-to-field, arithmetic operand) | **decline** | see the note below — this is not merely "out of scope", it is a correctness requirement |
| `MongoInExpression` | flip `Negated` (`$in` ↔ `$nin`) | `$nin` is defined as the complement of `$in` |
| `MongoRegexExpression` | flip `Negated` (`{f: {$not: /re/}}`) | `$not` is a set complement |
| `MongoElemMatchExpression` | flip `Negated` | `$not` complements the `$elemMatch`; the bare-`Any()` form flips `$exists` |
| `MongoUnaryExpression{Not}(X)` | `X`, provided `X` is itself in the admitted set | double negation — exact for any `X` |
| `MongoFieldExpression` (bare bool predicate) | `MongoUnaryExpression(Not, f)` → `{f: {$ne: true}}` | complement of `{f: true}`; the existing `RenderUnary` arm |
| anything else | **decline** (`false`, no output) | |

Confining operator inversion to the **one pair whose mirrored form provably partitions** (`$eq`/`$ne`) and
`$not`-wrapping the four whose mirrored form does not is the entire safety argument of this slice.

**Precise statement of the partition claim (spike-measured; the earlier wording was ambiguous).** Two different
pairings are in play, and only one of them fails:

| operator | (plain, `$not`-wrapped) | (plain, **mirrored**) | may the negator invert? |
|---|---|---|---|
| `$eq`, `$ne` | PARTITIONS | **PARTITIONS** | **yes** — mirroring is exact |
| `$lt`, `$lte`, `$gt`, `$gte` | PARTITIONS | **DOES NOT PARTITION** | no — must `$not`-wrap |

So `$not` over an operator document is an exact complement for **every** operator, at both element and
document-root scope (measured identically at both). What fails is only the *mirrored* pairing for the four
relational operators: `{Rank: {$gt: 5}}` ∪ `{Rank: {$lte: 5}}` misses every document whose `Rank` is missing,
null, **or of another BSON type**. That third state is a spike addition to this table — a string-valued `Rank`
behaves exactly like missing/null for every numeric operator (BSON type bracketing) and lands in the `$not` half
every time, so it needs no special handling but is worth knowing exists.

**Two illegal forms the spike found live, both now load-bearing constraints rather than style choices:**

1. **`{field: {$not: <bareValue>}}` is a server error** (`$not argument must be a regex or an object`). Because
   `RenderComparison` renders `Equal` as a *bare* `{field: value}`, a renderer arm that reused that output
   directly inside `$not` would emit exactly this illegal form. The `Equal` case must explicitly wrap in `$eq`.
   This is **reachable in practice**, via §6 flip 3 (`Where(x => !(x.A == 1))`, which EF does not normalize
   away), so it is not a theoretical concern.
2. **`{$not: {$or: […]}}` is rejected** (`unknown operator: $or`). De Morgan producing an `$or`/`$and` **array of
   negated conjuncts** — §3.2's third row — is therefore *mandatory*: the alternative of wrapping a conjunction
   in `$not` does not run at all.

A useful consequence of the design: the new renderer arm only ever sees a relational comparison *when driven by
the negator*, but it must still handle `Equal`/`NotEqual` correctly because the translator's own `Not` arm can
hand it those (flip 3).

**Both comparison rows are gated on `IsQueryNativeComparison` (bare field on the left, constant/parameter on the
right), and that gate is load-bearing rather than a scope statement.** A field-to-field comparison has no
query-dialect rendering at all: mirroring `Equal`→`NotEqual` on one would produce a node that `RenderNode` sends
to the `$expr` catch-all, and `$not`-wrapping one would produce a node the new `RenderUnary` arm throws on —
and inside `$elemMatch` the former is a hard server error, so it would fail at execution time rather than
decline. The negator must therefore reject a non-query-native comparison itself, not rely on the downstream
dialect gate to catch it. Implementation consequence: `IsQueryNativeComparison` is currently `private static` on
`MongoQueryLanguageRenderer` and must become visible to the negator (`internal static`), which also keeps the
single definition of "query-native" shared rather than duplicated.

**B — negate the LINQ expression tree (`Expression.Not(body)`) and lean on the existing `Not` arm (rejected as
the mechanism).** This is what `NativeCardinalityBinder` does today, and it is the thing being replaced. To
work it would need De Morgan and `Not`-over-comparison expressed *in the LINQ domain*, which is an open-ended
node space: lifted nullable operators, `Convert`/`ConvertChecked` wrappers, `Nullable<T>.HasValue`,
user-defined operators. The translated Mongo tree, by contrast, is a **closed set of seven node kinds that maps
one-to-one onto what the renderer can express**, so "is this an exact complement?" is decidable per node and
exhaustively unit-testable. Negating after translation also means the negator operates on an
already-scope-resolved tree, so it cannot reintroduce a scoping mistake.

**C — operator inversion (`$gt` → `$lte`, etc.) (rejected, and worth recording why).** This is *provably wrong*,
not merely risky. Neither `{Rank: {$gt: 5}}` nor `{Rank: {$lte: 5}}` matches an element whose `Rank` is missing
or explicitly null — the two do not partition the element space. So `All(p => p.Rank > 5)` would render as
`{Posts: {$not: {$elemMatch: {Rank: {$lte: 5}}}}}`, which for a document containing an element with no `Rank`
matches (nothing satisfied the `$elemMatch`) and therefore reports `All == true`, where LINQ evaluates
`null > 5` as `false` and answers `false`. Silent wrong data, under default `Native`, on an extremely ordinary
input. `$not` over the operator document is the exact complement and costs exactly one renderer arm.

### 3.2 Rendered forms

| LINQ | MQL |
|---|---|
| `b.Posts.All(p => p.Heading == "x")` | `{ "Posts": { $not: { $elemMatch: { "Heading": { $ne: "x" } } } } }` |
| `b.Posts.All(p => p.Rank > 5)` | `{ "Posts": { $not: { $elemMatch: { "Rank": { $not: { $gt: 5 } } } } } }` |
| `b.Posts.All(p => p.A == 1 && p.B > 2)` | `{ "Posts": { $not: { $elemMatch: { $or: [ {"A": {$ne: 1}}, {"B": {$not: {$gt: 2}}} ] } } } }` |
| `!b.Posts.All(p => p.Rank > 5)` | `{ "Posts": { $elemMatch: { "Rank": { $not: { $gt: 5 } } } } }` |
| `b.Fams.All(f => f.Kids.Any(k => k.Age > 3))` | `{ "Fams": { $not: { $elemMatch: { "Kids": { $not: { $elemMatch: { "Age": { $gt: 3 } } } } } } } }` |
| `ctx.Blogs.All(b => b.Rank > 5)` (EF-335) | `$match: { "Rank": { $not: { $gt: 5 } } }` + `$limit`/`$count`, presence-only |

Note the third row: De Morgan turns a merged single-document `$and` into an explicit `$or` array, which
`CombineOr` already renders.

### 3.3 Why the enclosing form is correct

`All(pred)` is true iff **no element satisfies `¬pred`**, which is exactly
`{ path: { $not: { $elemMatch: ¬pred } } }`.

- **empty array** → nothing satisfies `$elemMatch` → `$not` matches → `true`. LINQ `All` over an empty sequence
  is `true`. ✅
- **missing field** → same. EF materializes a missing embedded array as an empty list, so LINQ also says
  `true`. ✅
- **explicit BSON `null`** → same. ✅
- **`!All(pred)`** → drop the `$not` → `{ path: { $elemMatch: ¬pred } }`, i.e. the existing `Negated` flip. ✅

So the correctness burden reduces **entirely** to the negator's contract — "`¬pred` matches element `e` ⟺
`!pred(e)`" — and to nothing else. That reduction is the reason this design is defensible; §9 tests exactly
that proposition and nothing weaker.

## 4. Components

Four files touched, one new.

**1. `Query/NativeTranslation/MongoExpressionNegator.cs` (new).** `internal static`, the table in §3.1. Declines
by returning `false` with no output; a child decline propagates. Two invariants, both unit-tested (§9):

- **Input domain.** The negator's admitted set is a subset of `MongoQueryLanguageRenderer.IsQueryDialectRenderable`'s.
- **Output domain.** Every node the negator *produces* is admitted by `IsQueryDialectRenderable` and rendered by
  `RenderNode` **without** falling through to `$expr` and **without** throwing.

The output invariant is the load-bearing one: this negation is emitted inside `$elemMatch`, where `$expr` is a
hard server error (established by the `Any` slice's spike), so a negator that produced a node the renderer sent
to the `$expr` catch-all would make the whole query throw at execution time under `Native` as well as
`NativeOnly`.

**2. `Query/NativeTranslation/MongoQueryLanguageRenderer.cs`.** One new `RenderUnary` arm: a `Not` whose operand
is a **query-native** comparison (`IsQueryNativeComparison` — bare field on the left, constant/parameter on the
right) renders as `{ <elementName>: { $not: { <op>: <value> } } }`. The existing bare-bool-field arm and the
throw-for-everything-else are untouched. `IsQueryDialectRenderable` gets the matching arm; the file already
carries the "these two must change together" contract on both sides, and that comment is extended to the new
case.

**3. `Query/NativeTranslation/MongoExpressionTranslator.cs`.**
- `TryMatchAnyMethod` generalizes to `TryMatchQuantifierMethod`, additionally matching `All` and reporting which
  quantifier matched. `Any` keeps its 1-arg (bare) and 2-arg forms; **`All` has only the 2-arg form** — there is
  no parameterless `All`, so a 1-arg `All` is not a shape to handle.
- The quantifier arm: for `All`, resolve the array path and translate the element predicate with the **same**
  element-scoped child translator the `Any` path uses, then `TryNegate`, then build
  `MongoElemMatchExpression(arrayPath, ¬pred, negated: true)` and run the same `IsQueryDialectRenderable` gate
  before returning. A `TryNegate` decline returns `null` (clean fallback). The `Any` path is unchanged.
- `!All(...)` needs **no new code**: the existing `Not` arm's `MongoElemMatchExpression` `Negated` flip already
  produces the correct form.

**4. `Query/NativeTranslation/NativeCardinalityBinder.cs` (EF-335).** Replace the `Expression.Not(predicate.Body)`
+ translate sequence with translate-the-body-then-`TryNegate`, declining (`return false`, no mutation) if either
step fails. The negated conjunct still goes through `AddPredicateConjunct`, so the EF-347 tail-append semantics
(a predicate-injecting aggregate after `Take`/`Skip` evaluates over the paged rows) are unaffected. This is the
root scope, so index-first dialect mixing still applies: only the negated subtree carries `$not`, and any other
conjunct on the select stays a plain `$match` term.

**No new AST node, and no changes to** the array-path resolver (`TryResolveOwnedCollectionPath`),
`MongoFieldPrefixRewriter`, the lowerer, the pipeline factory, the shapers, or `StreamingEligibility`.

## 5. Guards — all inherited, none new

The `Any` slice's guards apply to `All` unchanged, because `All` reaches the same arm through the same path
resolution and the same element-scoped child translator:

- **`IsQueryDialectRenderable` on the final child** — a correctness gate, not an index preference: `$expr`
  inside `$elemMatch` is a hard server error.
- **`ReferencesEnclosingScope`** — a correlated element predicate (`Where(o => o.Items.All(i => o.Name == "x")))`)
  declines before the element-scoped translator is constructed. This guard is load-bearing for `All` for exactly
  the reason the `Any` slice documented: single-scope `TryResolveMember` resolves a member by **name**, so an
  enclosing-scoped access whose name also exists on the element would be silently retargeted at the element.
- **Single-scope only** (`_outerParam is null && _innerPrefix is null`) — a two-scope (`SelectMany`) quantifier
  declines.
- **Scope-relative array paths** via `GetContainingElementName()` — unchanged, and what makes nested quantifiers
  compose in either order.
- **`MongoFieldPrefixRewriter`** prefixes `ArrayPath` only, leaving the element predicate untouched.

The one genuinely new decline is *an element predicate that translates but has no exact complement*. Both
candidates for that are already declined further upstream: a field-to-field or arithmetic comparison by the
dialect gate, and a nullable bare bool by the translator's own bool acceptance
(`MongoExpressionTranslator.cs:341-343`).

## 6. This changes the eligibility set — and, unlike recent slices, it *could* change results

Three flips.

1. **Owned-collection `All` predicates → native.** Functional-only; Northwind has no owned collections, so this
   half contributes **zero** spec delta.
2. **Top-level `All` aggregate → native (EF-335)**, for a relational-comparison predicate and for `&&`/`||`
   combinations (`All(x => x.A > 1 && x.B == 2)` translates today and then throws in `RenderUnary`, so the gate
   falls back). **Spike correction — this half contributes ZERO spec delta, the opposite of what this section
   originally claimed.** The full Northwind `All` inventory was enumerated: `All_top_level` (a `StartsWith`
   predicate) is **already native today** and should emit byte-identical MQL after the swap, because the negator
   flips the same `MongoRegexExpression.Negated` the current `Expression.Not` path does; `All_top_level_column`
   is **field-to-field**, which the negator declines by design; and every remaining `All`-shaped spec test is
   either already native via `AllAnyToContainsRewriting`'s `$nin`, or unsupported for an unrelated reason
   (subquery, cross-collection nav, post-terminal, constant). So EF-335 is genuinely fixed, but Northwind
   happens to contain no top-level `All` with a relational-comparison predicate to demonstrate it — the fix is
   proven by functional tests, not by a spec flip. `All_top_level` becomes a **regression check** rather than a
   re-baselining target.

   **Correction (found during Task 5, superseding the "zero delta" claim above): there IS exactly one spec flip
   from this half — `NorthwindAggregateOperatorsQueryMongoTest.Select_All`.** The spike's inventory listed it
   among tests "currently failing under `NativeOnly`" and therefore expected to stay failing, which caused it to
   be missed: it is an **MQL-baseline** test that runs under default `Native`, so what changed is its *emitted
   pipeline*, not its `NativeOnly` pass/fail. Measured actual vs. baseline:

   ```
   baseline: Orders.{ "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
   actual:   Orders.{ "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$limit" : 1 }
   ```

   The `$match` is byte-identical; the only difference is the trailing `$project: {_id: 0, _v: null}`
   **disappearing**. That stage is the driver-LINQ fallback's scalar-placeholder projection, so its absence is
   the signature of the query now routing **natively** (the native presence-only aggregate derives its boolean
   from whether a row survived `$limit`, and needs no projection). Results are unaffected — the base EF Core
   result assertion passes and only the MQL string assertion fails. This is a **re-baseline, not a regression**,
   and is exactly what §6 predicted would be needed; the prediction was merely attributed to the wrong test.

   **Lesson worth keeping:** "already failing under `NativeOnly`" does **not** imply "will not flip". A test can
   be `NativeOnly`-failing and still have a `Native`-mode MQL baseline that this slice changes. Any future
   spec-delta inventory must check both axes per test, not just the `NativeOnly` pass set.
3. **Incidental widening: `Where(x => !(<comparison>))` — reachable, and wider than first stated.** Spike-
   confirmed: EF normalizes **nothing** (`!(a > b)`, `!(a == b)`, and `!(a && b)` all arrive intact), so this
   flip is real and needs deliberate functional coverage rather than a recorded note. And it is not limited to
   relational operators: `TranslateNode`'s `Not` arm already builds `MongoUnaryExpression(Not, <comparison>)` for
   **any** comparison operator, so once `RenderUnary` accepts those, **all six** of `!(a > b)`, `!(a >= b)`,
   `!(a < b)`, `!(a <= b)`, `!(a == b)`, `!(a != b)` go native — including the two equality forms, which are
   exactly the ones that hit illegal form 1 in §3.1. `Not` over `AndAlso`/`OrElse` still declines (its operand is
   not a comparison), so `Where`-level De Morgan is unaffected.

   **FINAL MEASURED ATTRIBUTION (Task 6, base-vs-branch in a worktree at `791037b`, both axes, per-test outcomes
   AND failure messages diffed) — this section's earlier guesses were wrong in both directions, so here is what
   was actually observed:**

   | source | predicted delta | measured delta |
   |---|---|---|
   | `All` aggregate (EF-335 half) | spike said **zero** | **2** — `Select_All(async: True/False)` |
   | flip 3 (`RenderUnary` widening) | spike said **this is the delta** | **zero** — no watch-list test flipped in either mode |

   **Total spec delta: exactly 2 tests, both pure MQL re-baselines of the same test.** Why flip 3 contributed
   nothing: of the 13 watch-list tests, 4 still decline because their `Not` operand is a conjunction or a nested
   `Not` (refused by design — `IsQueryDialectRenderable` admits `Not` only over a bare field or a query-native
   comparison), and the other 9 never failed at `RenderUnary` in the first place — provable because Task 2's edit
   to that throw string left their failure messages untouched. So the widening is real and functionally tested,
   but Northwind contains no query that exercises it.

   **A sweep artefact worth recording so a future run does not misread it:** 8 tests show a **message-only** diff
   (the extended `RenderUnary` throw string). Same exception type, same decline, same result — not flips.

**Risk-profile note, stated plainly because it differs from the recent stack.** Every recent slice here has been
"fallback→native, results structurally unchanged". This one is **not** structurally incapable of changing
results: a wrong complement returns wrong rows under default `Native`, where the pre-slice fallback was correct.
That is a fallback→native transition that *changes results* — precisely what the rubric's not-a-break carve-out
does **not** cover, and precisely the failure mode the `Any` slice shipped a critical fix for (`4f8c56c`). This
is why §9's differential test is the gate for the slice rather than a nice-to-have.

## 7. Non-goals (deferred — separate slices)

- **`.Count` in a predicate** (`Where(b => b.Posts.Count > 2)`) — a different decline site
  (`MongoExpressionTranslator.cs`'s intermediate-hop `IsCollection` guard) and a second dialect decision
  (query-dialect `$size`, which supports only exact size, vs. the `"path.n": {$exists: true}` index trick, vs.
  `$expr` + `$size`).
- **Embedded-collection projections** — `Select(b => b.Posts.Count)` (today's `$size` support is
  reference-collection/`$lookup`-alias only) and array projections `Select(b => b.Posts.Select(p => p.Title))`.
- **A correlated element predicate** — declined by `ReferencesEnclosingScope`. Supporting it needs more than a
  two-scope translator: `$elemMatch` cannot reference the enclosing document at all, so the correlated form would
  have to render as a top-level `$expr` over `$filter`/`$allElementsTrue`.
- **Two-scope (cross-scope `SelectMany`) quantifiers.**
- **Non-query-dialect element predicates** — `All(p => p.A > p.B)` and arithmetic element predicates stay
  fallback via the dialect gate.
- **Primitive-element collections and whole-element `All`.** EF Core's own
  `AllAnyToContainsRewritingExpressionVisitor` rewrites `All(x => x != c)` into `!Contains(c)` before the native
  translator sees it, so that spelling never reaches the quantifier matcher; it is handled (or declined) by the
  pre-existing `Contains`/`$in` path, orthogonally to this slice.
  **Spike correction — there is a second, different primitive shape that DOES reach the matcher:**
  `Tags.All(t => t == "x")` is *not* rewritten (the rewriter only handles `All(x => x != c)` / `Any(x => x == c)`)
  and arrives as `Enumerable.All(<MemberAccess>, <bare lambda>)` — the `Enumerable` spelling, an unquoted lambda,
  and **no `AsQueryable()` wrapper**, contradicting the "always `Queryable`, always quoted, always wrapped"
  finding the `Any` slice recorded. The generalized matcher will *match* it, and it declines one step later
  because `TryResolveOwnedCollectionPath` requires the final hop to be an embedded collection **navigation** and
  a primitive collection is a property (verified: `UnwrapAsQueryable` passes an unwrapped source through
  unchanged, so this is a clean decline, not a crash). It needs its own decline test, distinct from the
  `!= c` case.
- **Non-owned / reference-collection `All`** — the array-path resolver requires an embedded collection.
- **Index-friendliness of `$not`.** Not pursued, and the spike measured exactly what it costs (`queryPlanner`
  explain, 200 documents, indexes on `Rank` and multikey `Posts.Rank`):

  | filter | plan |
  |---|---|
  | `{Rank: {$gt: 150}}` (baseline) | IXSCAN |
  | **`{Rank: {$not: {$gt: 150}}}`** (the EF-335 root-scope emission) | **IXSCAN**, bounds `[MinKey, 150]` + `(inf, MaxKey]` |
  | `{Posts: {$elemMatch: {Rank: {$gt: 150}}}}` (the `Any` slice) | IXSCAN |
  | **`{Posts: {$not: {$elemMatch: {Rank: {$not: {$gt: 150}}}}}}`** (the `All` form) | **COLLSCAN** |

  So the root-scope half is index-usable after all — better than this section originally assumed — while the
  owned-collection `All` form is a collection scan. That asymmetry with the `Any` slice's index-first framing is
  deliberate: the index-friendly alternative (approach C) returns wrong answers, and correctness dominates. Note
  the already-shipped `!Any(...)` form is equally a COLLSCAN, so this introduces no *new* class of index
  regression. Worth an as-built note so the asymmetry is not later read as an oversight.
- **A general `Not`-over-arbitrary-subtree renderer.** The new arm covers `Not` over a query-native comparison
  only; `Not` over a non-query-native subtree still throws in `RenderUnary` and is still excluded by
  `IsQueryDialectRenderable`.

## 8. Slice 0 — throwaway de-risking spike — **RUN, verdict GO**

**Findings:** `.superpowers/sdd/2026-07-27-native-owned-collection-all/spike-findings.md` (gitignored). Verdict
**GO** — the gate did not fire, and **no operator needs to be declined**. Four corrections and two illegal-form
traps were folded back into §3.1, §6, §7 and §9 above; the items below record what each probe returned.

- **Item 1 ✅** `$not` over an operator document is **legal** inside `$elemMatch` (MongoDB 8.3.2) and returned
  exactly the predicted rows. `$expr` inside `$elemMatch` remains a hard server error, re-confirming §4.
- **Item 2 ✅** All six operators partition under `$not`-wrapping, at element **and** document-root scope,
  identically. Only the *mirrored* pairing fails, and only for the four relational operators — see §3.1's
  corrected table. Two illegal forms found (bare value under `$not`; `$not` over `$or`) — §3.1.
- **Item 3 ✅ with a correction** The owned-collection shape is exactly as predicted
  (`Queryable.All(Call(AsQueryable, [EF.Property chain]), Quote(lambda))`, 2-arg only, nesting either way). But
  a primitive-element `All(t => t == c)` arrives in a *different* shape — see §7.
- **Item 4 ✅ with a correction** EF normalizes **nothing**; the widening covers all six comparison operators —
  see §6 flip 3.
- **Item 5 ❌ — WRONG ON BOTH COUNTS; superseded by Task 6's measured base-vs-branch sweep.** The spike claimed
  (a) the `All` aggregate contributes zero spec delta and the delta comes from flip 3, and (b) a baseline of
  `Native` 4582/0/19, `NativeOnly` 2185/2397/19, total 4601, i.e. 7 fewer tests than
  `docs/native-query-status-EF-322.md` records. **Both were wrong:**
  - **Baseline:** Task 6 reproduced **4608** exactly (`Native` 4589/0/19, `NativeOnly` 2192/2397/19), matching the
    status doc verbatim. The spike's figures were 7 low. The status doc needed **no** correction, and the
    instruction to "correct" it would have *introduced* an error — Task 6 correctly declined to act on it.
  - **Delta attribution:** measured base-vs-branch in a worktree at `791037b`, across both axes, the attribution
    is the **exact inverse** of the spike's. See §6.
- **Item 6 ✅** Behavior-preserving for the already-native shapes: `All_top_level`'s regex predicate flips the
  same node either way.
- **Item 7 ✅** `$not` over `$elemMatch` behaves as predicted across populated / empty / missing / explicit-null
  arrays, and all 13 of §3.2's rendered forms matched an in-memory LINQ `All` oracle exactly on live data.
  Approach C, run for contrast, returned **2 wrong rows** — the design's central counter-example, reproduced.
- **Item 8 ✅ with a refinement** The driver emits `$expr: {$allElementsTrue: {$map: …}}` and throws
  `MongoCommandException` only on **array-level** missing/null — see §9.

### Original spike brief (retained for the record)

A throwaway branch, reverted after a written findings doc, per the project's spike-first practice (each of the
last three slices had a plan-invalidating surprise caught this way). In priority order:

1. **Is `{f: {$not: {<op>: v}}}` legal inside `$elemMatch`?** The single most load-bearing assumption in this
   design. `$expr` inside `$elemMatch` is a hard server error; if `$not`-over-operator-document is rejected too,
   the design must narrow. Verify against a real server, not from documentation.
2. **The exactness claims, live.** For each supported operator, confirm `{f: {$not: {<op>: v}}}` matches
   elements whose field is **missing**, **explicitly null**, and **of a different BSON type**, exactly as the
   complement predicts — and confirm the `$eq`/`$ne` pair genuinely partitions over those same states.
3. **EF's expression tree for `All` in a predicate.** Expected
   `Queryable.All(Call(AsQueryable, [<hop chain>]), Quote(lambda))`, 2-arg only, mirroring the `Any` finding.
   Also confirm `AllAnyToContainsRewritingExpressionVisitor` rewrites `All(x => x != c)` into `!Contains(c)`
   upstream, so primitive-element and whole-element `All` never reach the matcher.
4. **Does EF normalize `!(a > b)` into `a <= b`?** Determines whether flip 3 (§6) is reachable, and therefore
   whether it needs its own functional coverage or only a recorded note.
5. **Which Northwind spec tests flip**, and which of them assert MQL that needs re-baselining. This is the
   scoping input for the spec-sweep delta §6 requires be explained rather than asserted-zero.
6. **The top-level `All` swap is behavior-preserving** for the shapes already native today (regex, `$in`, bare
   bool): the same MQL, or a provably identical result set.
7. **`$not` over `$elemMatch` across array states** — re-confirm missing / `[]` / explicit-null / populated for
   the `$not`-wrapped form specifically (the `Any` slice confirmed the unwrapped `$elemMatch`).
8. **What the driver's LINQ v3 provider actually emits for `All`,** and whether it shares the
   `Any`/`Count` limitation the `Any` slice found (an `$expr`-based array operator that aborts the whole
   aggregate on a missing or explicitly-null array). §9's two-seed split assumes it does — expected to be
   `$allElementsTrue`, but the specific operator and the abort behavior should be observed rather than inferred,
   since it determines exactly which cases have a driver oracle.

**Gate:** if item 1 fails, narrow the slice to the operators whose complement needs no `$not` wrapper
(`==`/`!=`, `$in`, regex, nested quantifiers, bare bool), **decline relational comparisons**, and re-scope this
spec before touching production code. If item 2 contradicts any row of §3.1's table, that row's operator is
declined rather than supported.

## 9. Testing & verification

**The differential matrix test — the primary bar.** Table-driven, one row per supported element predicate
(`==`, `!=`, `<`, `<=`, `>`, `>=`, `&&`, `||`, `!`, `Contains`→`$in`, `StartsWith`, bare bool, nested `Any`,
nested `All`), executed as `Where(o => o.Items.All(pred))` under `MongoQueryMode.NativeOnly` against a seed
whose **elements** cover *value below / equal to / above the threshold, missing field, explicit BSON null*, and
whose **arrays** cover *multi-element / single / empty / missing / explicit null*.

The oracle is **in-memory LINQ over the materialized entities**: fetch the seed with a plain whole-entity native
query, then apply the same predicate client-side with `.All(...)`, and compare result sets. That oracle is
definitionally the semantics the provider promises, and it is **stronger than driver-LINQ here** — the driver
renders `All` as `$expr: {$allElementsTrue: {$map: …}}` (§8 item 8, captured) and throws
`MongoCommandException: $allElementsTrue's argument must be an array, but is null` the moment it evaluates
against a missing or null array.

**Spike refinement to the two-seed split — the driver oracle is usable more widely than assumed.** It is only
**array-level** missing/null that destroys it. With every array *present* but *elements* carrying a missing or
explicit-null field, driver-LINQ runs fine and agrees with both the in-memory oracle and the proposed native MQL.
So the well-formed parity seed **should include element-level missing/null values** — widening the driver-checked
surface beyond what the `Any` slice's equivalent seed covered, and putting a driver cross-check on exactly the
element states where a wrong complement would show up. Only the missing/null **array** rows need confining to the
`NativeOnly`-plus-hand-verified-expectation leg. The same table also runs for `Any`
(regression: that path must be byte-for-byte unaffected), for `!All` / `!Any`, and at **root scope** for the
EF-335 half.

This test is what turns the null/missing asymmetry from a review question into a build failure. For the fixture
to be able to express "missing element field", the relevant element properties are **nullable**; a
required-missing element field is a separate, pre-existing materialization concern (it throws in every mode) and
is documented rather than exercised here.

**Alongside it, in `NativeOwnedCollectionAllTests` (functional):**
- The `Any` slice's **two-seed** pattern — a well-formed-only seed for `Native == DriverLinq` parity, and the
  full state matrix proven by hand-computed expectations.
- MQL assertions pinning **both** levels: the enclosing `{path: {$not: {$elemMatch: …}}}` and the inner
  `{f: {$not: {$gt: v}}}`, plus the De Morgan `$or` for a conjunctive predicate.
- Negated `All`; nested `All`-in-`Any`, `Any`-in-`All`, `All`-in-`All`; reached-through-owned-ref
  (`b.Home.Notes.All(...)`); tracked **and** no-tracking.

**Unit tests:**
- `MongoExpressionNegator` exhaustively, one case per row of §3.1 **plus every decline**, and the two invariants
  of §4 asserted directly: the output of every successful negation is admitted by `IsQueryDialectRenderable`
  **and** renders through `RenderNode` without `$expr` and without throwing.
- The three-way pairing (`negator` ↔ `IsQueryDialectRenderable` ↔ `RenderNode`) pinned by a test, extending the
  pairing test the `Any` slice added.
- Renderer document shapes for the new `Not`-over-relational-comparison arm.
- `TryMatchQuantifierMethod` for `All`'s 2-arg form, the `Any` forms (unchanged), and the absence of a 1-arg
  `All`.

**Per the `Any` slice's process lesson, every guard is proven by deleting it and watching a test go red** — three
tests in that slice initially passed with the guard they nominally tested removed. A fixture that cannot expose
the bug makes the test vacuous.

**Decline tests:** field-to-field, arithmetic, correlated element predicate, two-scope quantifier, `.Count`,
reference (non-owned) collection — each asserted to **throw cleanly** under `NativeOnly` (a decline, not a
crash) and to return **correct results** under `Native`.

**EF-335 tests:** top-level `All` for a relational predicate, for `&&` and `||`, and for the shapes already
native today (regex, `$in`, bare bool) asserted unchanged.

**Sweeps:** full `/test-all` green on EF8 / EF9 / EF10 (foreground, per-version isolated testcontainers); the
`NativeOnly` EF10 spec sweep run and its **nonzero** delta enumerated flip-by-flip and explained.

**Hygiene:** all new types `internal`; `#if`-clean across EF8 / EF9 / EF10; nullable-annotated; a `Query/AGENTS.md`
as-built note covering the negator and its exact-complement contract, why `$eq`/`$ne` may be inverted while
relational operators may not, the renderer/classifier/negator three-way sync requirement, the EF-335 closure, the
`$not`-is-not-index-friendly asymmetry with the `Any` slice, and the deferral list.
