# Native owned-collection `.Count` in a predicate — design

*Epic EF-322 (native LINQ query provider). Owned-data translator slice, following the owned-collection `All`
(negated `$elemMatch`) slice.*
*Branch `EF-322-owned-collection-count-native`, stacked on the native tip `c19c99b`
(`origin/NativeQueryOngoing`).*
*A JIRA number should be filed for this slice; this doc will be updated with it.*

---

## 1. Problem

`ctx.Blogs.Where(b => b.Posts.Count > 2)` falls back to driver-LINQ even under `Native` mode, where `Posts` is
an owned (embedded) collection navigation. The two preceding slices made owned-collection `Any` and `All`
native, and both named this shape as their first explicit deferral. From
`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (the owned-collection `All` note, §"Deferred"):

> **`.Count` in a predicate** (`Where(b => b.Posts.Count > 2)`) — a different decline site (the
> intermediate-hop `IsCollection` guard in `MongoExpressionTranslator`) and a second dialect decision
> (query-dialect `$size`, which supports only exact size, vs. the `"path.n": {$exists: true}` index trick,
> vs. `$expr` + `$size`).

**Where it declines today.** `b.Posts.Count > 2` reaches
`MongoExpressionTranslator.TranslateComparison` (`MongoExpressionTranslator.cs:394`). Its first branch (the
query-native shape) calls `TryResolveMember` on the left operand, which delegates to
`TryResolveOwnedFieldPath` (`:635`); that walk requires every non-leaf hop to be an embedded **non-collection**
navigation and requires a mapped scalar `IProperty` leaf. `Posts` is a collection and `Count` is not a mapped
property, so it declines twice over. The second branch (`TranslateOperand`, `:510`) then also declines, because
`Count` is neither a resolvable member, nor arithmetic, nor a constant/parameter. `TranslateComparison` returns
`null`, the predicate is not natively representable, and the query falls back.

This is a clean decline today — no wrong `$match` is emitted, and the shape returns correct results via
driver-LINQ.

**Why it is worth a slice.** A count threshold on an embedded collection is among the most ordinary predicates
a document model produces (`Where(o => o.Items.Count > 1)`), and it is the last owned-collection *predicate*
shape on the deferred list that has a well-defined server-side form. It is also the shape that decides how this
provider expresses array cardinality generally, which the deferred embedded-collection *projection* slice will
inherit.

## 2. Goal & success criteria

**Goal.** Make an owned-collection element count, compared against a value, go native in a predicate — for all
six comparison operators, both operand orders, both a constant and a parameterized threshold, and all three
spellings (`.Count` property, `.Count()`, `.LongCount()`).

**Success criteria.**

1. `Where(b => b.Posts.Count > 2)` and the five other comparison operators succeed under
   `MongoQueryMode.NativeOnly` (the only reliable "went native" signal — see `Query/AGENTS.md`, *How to test*).
2. Results are **identical** to an in-memory LINQ oracle over the same `Expression`, across the full array-state
   matrix (multi / single / empty / missing field / explicit BSON `null`) and the full threshold-edge matrix
   (`0`, `1`, `n-1`, `n`, `n+1`).
3. A constant-threshold count nested inside a quantifier (`b.Posts.Any(p => p.Comments.Count > 2)`) goes native
   — it must not become a runtime `$expr`-inside-`$elemMatch` server error.
4. Bare `Any()` / `!Any()` emit **byte-identical** MQL to the pre-slice build, after being re-expressed through
   the new machinery (§3.5).
5. Every unsupported shape **declines cleanly** — falls back with correct results under `Native`, throws
   `NativeTranslationNotSupportedException` under `NativeOnly` — never returns wrong rows and never throws at
   execution time.
6. Three-version `/test-all` green with zero failures; the EF10 spec sweep accounted for on **both** axes
   (`NativeOnly` pass/fail *and* `Native`-mode MQL baselines — see §10).

## 3. Approach

### 3.1 The dialect decision

Three candidate renderings exist for an array-cardinality predicate. They are **not** equivalent.

**(a) Query-dialect `$size` — rejected, because it is semantically wrong, not merely limited.**
`{Posts: {$size: 3}}` matches only an array of exactly three elements, so it cannot express any relational
operator. Worse, `{Posts: {$size: 0}}` does **not** match a document whose `Posts` field is *missing* or
explicitly `null`, but LINQ's `Count == 0` is `true` for both — EF materializes a missing embedded array as an
empty list. That is the same class of bug as the `{path: {$ne: []}}` form the `Any` slice rejected for bare
`Any()`, and for the same reason. `$size` also cannot use an index.

**(b) The array-index `$exists` trick — chosen as the primary form.**
`{"Posts.n": {$exists: true}}` is true for exactly those documents whose array has more than `n` elements, so a
count threshold against a **known integer** is expressible for every comparison operator (§3.2). It is correct
for missing, `null`, and empty arrays for free (none of them has an element at any index), and it is pure query
dialect and therefore legal inside `$elemMatch`.

**It is NOT index-usable — measured, correcting this section's original claim.** The Task 1 spike ran
`explain("queryPlanner")` over 200 seeded documents with both a collection-level (`{"Posts": 1}`) and a
leaf-level (`{"Posts.Rank": 1}`) multikey index present, for all four relational forms: **every one came back
COLLSCAN**, with `rejectedPlans: []` — the planner did not even generate an IXSCAN candidate. That is consistent
with how MongoDB indexes multikey arrays (per-element values, not existence-at-ordinal), so it is unsurprising in
hindsight, but this section originally assumed the opposite and the driver's use of the same form is *not*
evidence of indexability. The choice stands on the other two grounds, which are the load-bearing ones: it is the
only form legal inside `$elemMatch`, and the only one correct for a missing or explicitly-null array.

Two independent precedents confirm it: the provider **already emits this form** for bare `Any()`
(`MongoQueryLanguageRenderer.cs:307-309` — `{"path.0": {$exists: !Negated}}`), and the C# driver's own LINQ v3
provider emits it for a reference-collection count — visible in a committed spec baseline,
`NorthwindIncludeNoTrackingQueryMongoTest.cs:493`:

```
{ "$match" : { "_lookup_Orders.2" : { "$exists" : true } } }
```

`Count >= 1` and bare `Any()` are therefore *the same predicate*, which §3.5 makes structural rather than
coincidental.

**(c) `$expr` + `$size` — chosen as the fallback tier, for what (b) structurally cannot express.**
The array index is a segment of the **field path**, not a value, and `PlaceholderTable` substitutes values, not
field names. So a **parameterized** threshold (`Count > someLocal`, which EF Core parameterizes by default)
cannot use (b) at all. `{$expr: {$gt: [{$size: {$ifNull: ["$Posts", []]}}, @n]}}` handles it, at the cost of a
COLLSCAN, and is illegal inside `$elemMatch`.

The `$ifNull` is **mandatory, not defensive**: `{$size: "$Posts"}` against a document whose `Posts` is missing
or `null` is a hard server error that **aborts the whole aggregate command** — the exact driver-LINQ failure
mode the `Any` slice documented and measured. `$ifNull` maps both states to `[]`, giving `0`, which is what
LINQ answers.

This two-tier split is the provider's established dialect policy applied unchanged: prefer the index-usable
query dialect, use `$expr` as the last resort, and decide **per subtree in the renderer**. See `Query/AGENTS.md`,
*Per-subtree boundary is index-first, and the renderer — not the lowerer — owns it*.

### 3.2 Rendered forms

Let `C` be the element count and `n` the threshold. `P` is the array path.

| Predicate | Constant tier (query dialect) | Valid for |
|---|---|---|
| `C > n` | `{"P.n": {$exists: true}}` | `n ≥ 0` |
| `C >= n` | `{"P.(n-1)": {$exists: true}}` | `n ≥ 1` |
| `C < n` | `{"P.(n-1)": {$exists: false}}` | `n ≥ 1` |
| `C <= n` | `{"P.n": {$exists: false}}` | `n ≥ 0` |
| `C == n`, `n ≥ 1` | `{"P.(n-1)": {$exists: true}, "P.n": {$exists: false}}` | `n ≥ 1` |
| `C == 0` | `{"P.0": {$exists: false}}` | — |
| `C != n`, `n ≥ 1` | `{$or: [{"P.(n-1)": {$exists: false}}, {"P.n": {$exists: true}}]}` | `n ≥ 1` |
| `C != 0` | `{"P.0": {$exists: true}}` | — |

The `==` and `!=` forms are built by handing the two single-key documents to the renderer's existing
`CombineAnd` / `CombineOr` helpers (`MongoQueryLanguageRenderer.cs:376`, `:459`) — `CombineAnd` merges two
distinct top-level keys into one document, which is where the merged `==` form above comes from. No new
combining logic.

**The admissibility rule is one sentence:** the threshold must be an **integer-valued** constant, and every
array index the form needs must be `≥ 0`. That single condition is what `IsQueryDialectRenderable` tests, and it
is what guarantees the renderer can never compute a negative or fractional path segment.

A threshold that falls outside the rule is **degenerate**, not unsupported: `C >= 0` and `C > -1` are
tautologies, `C < 0` and `C == -1` are contradictions, and a non-integer threshold (`C > 2.5`) has no index
form at all. None of these needs clamping logic or a decline — the classifier rejects them, and the `$expr`
tier below renders every one of them correctly and generally, **for the tautology/contradiction/parameterized
cases**. The non-integer case is different in one respect, measured rather than assumed: C# promotes the
`int` count via a compiler-inserted `Convert(count, double)`, and `TranslateOperand`'s convert guard (called
with `allowNumericWidening: false` on the comparison path) rejects that convert before the count is ever
recognized as an operand — so `Where(b => b.Posts.Count > 2.5)` written as ordinary LINQ falls back to
driver-LINQ entirely; it never reaches this translator, let alone the `$expr` tier. `TryGetIntegerThreshold`'s
own non-integral rejection is real code, but it is reachable only from a hand-built expression tree (the same
class of statement as the `Count() > 0` upstream-rewrite finding recorded in `Query/AGENTS.md`).

| Predicate | Fallback tier (aggregation dialect, inside `$expr`) |
|---|---|
| `C <op> @n` (parameter) | `{$expr: {<op>: [{$size: {$ifNull: ["$P", []]}}, @n]}}` |
| `C <op> n` (degenerate `n`) | same, with `n` baked in |
| `C1 <op> C2` (count vs count) | same, with `$size` on both sides |
| arithmetic on either side | same, composed through the existing operand machinery |

The last two rows are not scope creep — they fall out of recognizing the count as an ordinary **operand**
(§3.3), and the `$expr` renderer already handles arbitrary operand trees.

### 3.3 One dialect-neutral shape; the renderer picks the tier

Recognition goes in **`TranslateOperand`** (`MongoExpressionTranslator.cs:510`), not in `TranslateComparison`,
so a count is usable anywhere an operand is. `b.Posts.Count > 2` then translates to:

```
MongoBinaryExpression(GreaterThan,
    MongoSizeExpression("Posts", nullSafe: true),   // value node, Type = int
    MongoConstantExpression(2))
```

That tree is **dialect-neutral** — it says "the size of this array, compared to this value" and nothing about
`$exists` or `$expr`. The renderer decides:

- `MongoQueryLanguageRenderer` recognizes a size-vs-**constant** comparison whose indices are all `≥ 0` and
  emits the §3.2 constant form.
- Everything else falls through the **existing** `RenderAsExpr` catch-all to
  `MongoAggregationExpressionRenderer`, which needs one new arm for the null-safe `$size` (§4).
- `IsQueryDialectRenderable` returns `true` for exactly the constant-tier forms — which is what makes a
  parameterized count nested in a quantifier decline **with no new guard**, since that classifier is already the
  gate the quantifier arm consults (`MongoExpressionTranslator.cs:353`).

**Operand order.** `TranslateComparison`'s second branch deliberately preserves operand order (no mirroring, so
non-commutative comparisons keep their order inside `$expr`). For the count shape the translator therefore
**normalizes the size node to the left**, mirroring the operator via the existing `Mirror` helper (`:1015`),
whenever the other side is a plain value. That keeps the renderer's recognition to a single left-side pattern
rather than two.

### 3.4 Reusing `MongoSizeExpression` rather than adding a second `$size` node

**This is a refinement made during design review.** The provider already has a `$size` value node —
`Expressions/MongoSizeExpression.cs`, used for a projected reference-collection `Count`
(`NativeProjectionBinder.cs:222`), rendered at `MongoAggregationExpressionRenderer.cs:51` as
`{$size: "$<FieldName>"}`. Adding a second node meaning "the size of an array" would be exactly the duplication
this slice's bare-`Any()` unification (§3.5) exists to remove — and that node's own doc comment already
anticipates a second use:

> Holding the raw field name directly avoids inventing a second synthetic field-reference node for a single,
> narrow use.

So `MongoSizeExpression` is **generalized** instead: `FieldName` documented as a dotted array path (it already
is one, structurally), plus one new property `NullSafe`.

- `NullSafe: false` (the default, preserving today's construction) → `{$size: "$P"}`. Correct for the existing
  use, where `P` is a `$lookup` output alias and the array therefore always exists.
- `NullSafe: true` (this slice) → `{$size: {$ifNull: ["$P", []]}}`. Required for an **embedded** array, which
  can be missing or `null`.

The flag exists specifically so the shipped projection path's emitted MQL stays **byte-identical** — several
committed spec baselines pin `{ "$size" : "$_lookup_Orders" }` and must not move. Always emitting `$ifNull`
would be semantically equivalent for the lookup case but would churn those baselines for no benefit.

### 3.5 The bare-`Any()` unification

Since `Count >= 1` and `Any()` are the same predicate (§3.1), the translator's bare-quantifier arm
(`MongoExpressionTranslator.cs:311-312`) stops constructing `MongoElemMatchExpression(path, null, negated)` and
constructs the size comparison instead. Consequently:

- `MongoElemMatchExpression.ElementPredicate` becomes **non-nullable**, and the node means exactly one thing:
  `$elemMatch`. Its renderer loses the null branch, its `IsQueryDialectRenderable` arm loses the
  `{ElementPredicate: null}` case, and its negator case keeps flipping `Negated` only.
- `!Any()` needs no dedicated handling: the negator inverts `>=` to `<` (§5.1), giving `C < 1`, which renders
  `{"P.0": {$exists: false}}`.
- The emitted MQL for both forms is unchanged, and that byte-identity is the regression net for the refactor —
  the shipped `Any`/`All` suites (36 functional tests, the unit tests, and the differential matrix) are exactly
  the coverage that proves it.

The alternative — two nodes that happen to render the same document — would leave the equivalence as a
coincidence pinned only by a test. This makes it structural.

## 4. Components

| File | Change |
|---|---|
| `Expressions/MongoSizeExpression.cs` | Add `NullSafe`; document `FieldName` as a dotted array path (§3.4) |
| `Expressions/MongoElemMatchExpression.cs` | `ElementPredicate` becomes non-nullable (§3.5) |
| `NativeTranslation/MongoExpressionTranslator.cs` | Recognize the count shapes in `TranslateOperand`; normalize the size node left; bare-quantifier arm emits a size comparison; `Not` arm delegates to the negator for a size comparison |
| `NativeTranslation/MongoQueryLanguageRenderer.cs` | New constant-tier rendering (§3.2); `IsQueryDialectRenderable` arm; `RenderElemMatch` loses its null branch |
| `NativeTranslation/MongoAggregationExpressionRenderer.cs` | `MongoSizeExpression` arm honours `NullSafe` |
| `NativeTranslation/MongoExpressionNegator.cs` | Size-comparison case — invert the operator (§5.1) |
| `NativeTranslation/MongoFieldPrefixRewriter.cs` | `MongoSizeExpression` case — prefix `FieldName` |

**The walker checklist.** Every tree-walker that can now meet a `MongoSizeExpression` in a *predicate* position
must learn it, because a missed walker is a throw or a silent misbehavior, not a clean decline:

| Walker | Required behavior |
|---|---|
| `MongoQueryLanguageRenderer.RenderNode` | Constant tier, else `RenderAsExpr` |
| `MongoQueryLanguageRenderer.IsQueryDialectRenderable` | `true` only for the admissible constant forms |
| `MongoAggregationExpressionRenderer` | `$size` honouring `NullSafe` |
| `MongoExpressionNegator` | Invert the operator (exact — §5.1) |
| `MongoFieldPrefixRewriter` | Prefix `FieldName`; **do not** descend into an `$elemMatch` element predicate |
| `MongoExpressionTranslator`'s `Not` arm | Delegate to the negator for this node kind |
| `MongoExpressionTranslator.AllFieldsDefaultSerialized` | No change needed — its `_ => true` catch-all (`:202`) already passes a size node, correctly: a count carries no property serialization |

Path resolution reuses `TryResolveOwnedCollectionPath` (`:944`) **unchanged** — the same scope-relative resolver
the `Any`/`All` quantifiers use, which is what makes counts through owned single-reference hops
(`b.Home.Notes.Count`) and counts nested inside a quantifier resolve correctly.

Nothing else changes: no lowerer change, no pipeline-factory change, no shaper change, no
`StreamingEligibility` change, no new AST node, no `#if`, no public API, no annotation.

## 5. Correctness rules

### 5.1 Negation inverts the operator — the documented exception to the `All` slice's rule

The `All` slice established, and proved on live data, that MongoDB's four relational operators must be
`$not`-wrapped and **never** inverted, because `{Rank: {$gt: 5}}` and `{Rank: {$lte: 5}}` do not partition the
value space (neither matches a field that is missing, `null`, or of another BSON type).

**That rule does not apply here, and the reason is the same test with the opposite answer.** A size comparison
against an admissible constant renders as `$exists` on a specific path, and `$exists` **does** partition: every
document either has `P.k` or does not. So for a fixed `k`, `{"P.k": {$exists: true}}` and
`{"P.k": {$exists: false}}` are exact complements, and inverting the operator is exact:

```
C > n   ↔  C <= n        C >= n  ↔  C < n        C == n  ↔  C != n
```

The rule stays in `MongoExpressionNegator` — the established single home for "exact complement or decline" — and
the translator's `Not` arm delegates there rather than duplicating it. This matters beyond `!(…)`: `All` negates
its element predicate through the negator, so `b.Posts.All(p => p.Comments.Count > 2)` depends on this case.

**The admitted set is closed under inversion**, which is what makes delegation safe: `C > n` (`n ≥ 0`) inverts
to `C <= n` (`n ≥ 0`); `C >= n` (`n ≥ 1`) to `C < n` (`n ≥ 1`); `C == n` to `C != n` (same `n`). Every inverse is
itself admissible, so the negator can never hand the renderer a form the classifier would have rejected. This is
a property to unit-test directly, mirroring the existing
`MongoExpressionNegatorTests.Negation_is_an_involution_on_the_supported_set`.

Getting this backwards in **either** direction is silent wrong data — `$not`-wrapping where inversion is exact
merely loses an index, but inverting where it is not exact returns wrong rows. Both the rule and its
justification therefore go in the code, not just here.

### 5.2 Missing, explicit-`null`, and empty arrays are all `Count == 0`

EF materializes a missing embedded array as an empty list, so LINQ's `Count` is `0` for all three states. The
constant tier gets this right for free (no index exists in any of them). The `$expr` tier needs `$ifNull`, whose
absence is not a slow path but a hard server error that aborts the aggregate (§3.1c).

### 5.3 Degenerate thresholds need no decline

Rejected by the classifier, rendered correctly by the `$expr` tier (§3.2) — for the tautology, contradiction,
and parameterized cases. The one consequence is that a degenerate constant nested inside `$elemMatch`
declines — rare, and safe.

**The non-integral case is not actually reached via ordinary LINQ (measured, corrects an earlier assumption
in this section).** `Where(b => b.Posts.Count > 2.5)` never reaches this translator at all: C# promotes the
`int` count via a compiler-inserted `Convert(count, double)`, and `TranslateOperand`'s convert guard (called
with `allowNumericWidening: false` on the comparison path) rejects that convert first, so the whole predicate
falls back to driver-LINQ before the count is even recognized as an operand. `TryGetIntegerThreshold`'s own
non-integral rejection is real code, but it is reachable only from a hand-built expression tree — the same
class of statement as the `Count() > 0` upstream-rewrite finding recorded in `Query/AGENTS.md`.

### 5.4 `$expr` self-gates out of `$elemMatch`

`$expr` inside `$elemMatch` is a hard server error, so this is a correctness gate, not an index preference. No
new guard is needed: the quantifier arm already declines when its child fails
`IsQueryDialectRenderable` (`MongoExpressionTranslator.cs:349-354`), and the parameterized tier fails it by
construction.

### 5.5 Guards inherited unchanged, and one accepted asymmetry

**Inherited:** the two-scope decline (`_outerParam is not null || _innerPrefix is not null`,
`MongoExpressionTranslator.cs:952`) — so a count inside a `SelectMany` unwind translator still declines;
`ReferencesEnclosingScope` for a correlated element predicate; scope-relative array paths;
`MongoFieldPrefixRewriter` prefixing the array path only.

**Accepted asymmetry, to be documented in `Query/AGENTS.md`:** `Count <= @param` goes native, but
`!(Count > @param)` declines — the negator gates on `IsQueryDialectRenderable` at entry, and the parameterized
tier is not query-dialect. Inversion *would* be exact there (both `$expr` operands are always numbers, thanks to
`$ifNull`), so this is a coverage gap, not a correctness one. Widening the negator's entry gate is a separate
change, deliberately not taken here: that gate is what makes the negator's output-domain invariant
unconditional, and the `All` slice's whole safety argument rests on it.

## 6. This changes the eligibility set — and it could change results

Per the versioning rubric (`AGENTS.md`), a fallback → native transition with unchanged results is **not** a
breaking change, and the emitted MQL for a supported query is not part of the contract.

But this slice is **not** structurally incapable of changing results — the same risk profile the `All` slice
carried, for a similar reason. Three specific ways it could:

1. **A wrong index computation** returns wrong rows under default `Native`, where the pre-slice fallback was
   correct. Off-by-one on any of the eight rows in §3.2 is silent.
2. **A wrong negation direction** (§5.1) is silent in the same way.
3. **The bare-`Any()` unification touches shipped behavior.** If the re-expressed form is not exactly
   `Count >= 1`, every `Any()`/`!Any()` query changes.

So the primary verification bar is **not** driver parity but a differential test against an in-memory oracle
(§9), and (3) is additionally covered by MQL byte-identity assertions.

## 7. Non-goals (deferred — separate slices)

- **Primitive-element collections** (`b.Tags.Count > 2`, where `Tags` is a mapped `List<string>` property, not a
  navigation). `TryResolveOwnedCollectionPath` requires an embedded collection **navigation** and declines a
  primitive collection property; the `Any`/`All` slices decline the same shape. The rendering would be
  identical, so the right slice is one that lights up all three quantifiers plus `.Count` for primitive
  collections at once, via a second resolver branch keyed on the property's element name.
- **Filtered `Count(pred)`** (`b.Posts.Count(p => p.Rank > 1) > 2`). No index-trick form exists; it needs
  `$expr` over `$filter`, a third mechanism reachable from neither tier, and it shares the correlated-element
  problem already deferred on the `Any`/`All` side.
- **Reference-collection `.Count`** (`c.Orders.Count > 2`). Needs the `$lookup` machinery; reference-nav lookups
  are deferred stack-wide, and driver-LINQ already renders this shape.
- **`.Count` in a projection** (`Select(b => b.Posts.Count)`) — the separately-deferred embedded-collection
  projection slice, which will inherit this slice's `NullSafe` `$size` (today's projected-`Count` support is
  `$lookup`-alias only).
- **Two-scope (cross-scope `SelectMany`) counts** — the inherited blanket decline (§5.5).
- **`!(Count > @param)`** — the accepted asymmetry (§5.5).
- **Index-friendliness of the `$expr` tier** — not pursued; it is a COLLSCAN by construction, as the `All`
  slice's `$not`/`$elemMatch` form is.

## 8. Slice 0 — de-risking spike (run before implementation)

Three items. Two are genuine unknowns; the third was resolved by inspection during design and is recorded so it
is not re-investigated.

**Item 1 — the EF expression-tree shape for an owned-collection `.Count` (BLOCKING).** Capture the actual tree
for `Where(b => b.Posts.Count > 2)`, `Where(b => b.Posts.Count() > 2)`, and
`Where(b => b.Posts.LongCount() > 2L)`, on **EF8, EF9, and EF10**. Specifically: does `.Count` arrive as a
`MemberExpression` on `List<T>`/`ICollection<T>`, or does EF rewrite it to `Enumerable.Count`/`Queryable.Count`?
Is the source wrapped in `AsQueryable()`? Is the threshold a `ConstantExpression` or an EF query parameter, for
an inline literal versus a captured local?

This item is blocking because the `All` slice's own lesson was that a plausible tree-shape assumption can be
wrong in a plan-invalidating way: the `Any` slice recorded "always the `Queryable` overload, always `Quote`-
wrapped, always exactly one `AsQueryable()`", and the `All` slice found a primitive-collection quantifier
arriving as `Enumerable.All(<MemberAccess>, <bare unquoted lambda>)` with no wrapper at all. Assume nothing;
measure all three EF versions.

**Item 2 — is `{"P.n": {$exists: true}}` genuinely IXSCAN-capable on a multikey index?** Run a `queryPlanner`
explain against a live server over a seeded collection with a multikey index on the embedded array, for each of
the four relational forms in §3.2, and record the winning plan and index bounds. The `All` slice measured its
own index behavior and found one form to be a COLLSCAN despite looking index-shaped, so "the driver emits it, so
it must be indexed" is not evidence. If the answer is COLLSCAN, the design does **not** change — the constant
tier is still required for `$elemMatch` legality and still correct — but §3.1's index claim must be corrected
rather than repeated.

**Item 3 — a property-less parameter threshold (RESOLVED by inspection, verify in a test).**
`MongoValueRenderer.RenderValue` (`MongoValueRenderer.cs:52-53`) already handles a `MongoParameterExpression`
whose `ForSerialization` is `null` by calling `placeholders.CreatePlaceholder(parameter.Name, serializer: null)`,
and `TranslateOperand` (`:540-542`) already documents property-less numeric operands as serializing via
`BsonValue.Create`. `Skip`/`Take` counts are the established precedent. No spike work needed; a functional test
with a captured-local threshold pins it.

**Verdict gate.** Item 1 must produce a concrete, version-checked tree shape before Task 2 starts. Item 2 is
informational — it changes documentation, not design.

## 9. Testing & verification

**The primary gate is a differential matrix test, not driver parity** (§6). It sends the *same* `Expression`
object to the server and compiles it for client-side evaluation, which is what makes it a real differential test
rather than two hand-written predicates that can silently diverge — the mechanism the `All` slice introduced as
`Quantifier_result_equals_the_in_memory_oracle_for_every_element_and_array_state`.

**Matrix.** Six comparison operators × {constant, parameter} thresholds × threshold edges {`0`, `1`, `n-1`, `n`,
`n+1`} against a seed whose arrays cover {multi, single, empty, missing field, explicit BSON `null`}. Plus
regression rows for bare `Any()`, `!Any()`, `Any(pred)` and `All(pred)` that must be byte-for-byte unaffected.

**Two seeds, as the `Any` slice established.** The full state matrix is proven via `NativeOnly` routing plus the
in-memory oracle, because driver-LINQ renders a count as `$size` under `$expr` and **aborts the aggregate** on a
missing or `null` array — so it cannot oracle the full matrix. A second, well-formed-only seed keeps an
independent `NativeOnly == DriverLinq` parity check for the ordinary rows.

**MQL assertions.** One per operator pinning the index arithmetic (`{"Posts.2": {$exists: true}}` for
`Count > 2`, and so on, including the merged `==` document and the `$or` `!=` form); one pinning the `$expr` tier
including `$ifNull`; and assertions that bare `Any()`/`!Any()` MQL is unchanged from the pre-slice build.

**Unit tests.** The §3.2 table row by row in `MongoQueryLanguageRendererTests`; the negator's inversion and its
closure-under-inversion property in `MongoExpressionNegatorTests`; the classifier's admissibility rule
(including every degenerate rejection, `≥ 0` and integer-valued alike); the prefix rewriter's `MongoSizeExpression` case; and the existing
five bare-`Any()` unit-test sites repurposed to the new representation
(`MongoExpressionNegatorTests.cs:184`/`:312`, `MongoQueryLanguageRendererTests.cs:569`/`:579`/`:770`).

**Decline tests.** Each deferred shape in §7 asserted to throw cleanly under `NativeOnly` **and** return correct
results under `Native` — the pattern the `All` slice used, which proves the decline is a decline and not a
crash.

**Mutation checks, with measured A/B.** The `All` slice's lesson was that a predicted mutation can be red for an
unrelated reason and therefore prove nothing, so each check must be demonstrated green-before / red-after:

1. Flip the negator's size case from inversion to `$not`-wrapping → rows must go red.
2. Introduce an off-by-one in one §3.2 row → the differential matrix must go red on the threshold-edge rows
   specifically.
3. Remove `$ifNull` from the `$expr` tier → the missing/`null`-array rows must fail (an aborted aggregate, not a
   wrong row).

**Suite discipline.** An eligibility-changing task must run the **functional** suite, not just unit tests — the
`All` slice shipped a red functional test from a task that was 635/635 green on units. Final verification is a
three-version `/test-all` with zero failures plus the EF10 two-sweep spec measurement.

## 10. Expected flips

**Spec suite.** Northwind has no owned collections, so the *owned* half of this slice should produce **zero**
spec delta. But that prediction is exactly the one the `All` slice's spike got wrong, and for a reason that
applies here too: an inventory built only from the `NativeOnly` pass set misses a test that is
`NativeOnly`-failing *and* has a `Native`-mode MQL baseline the slice changes. **Both axes must be checked per
test.** The bare-`Any()` unification is the specific reason to expect possible movement — any spec baseline
containing a `"path.0": {$exists: …}` document is a candidate, even though the emitted MQL is intended to be
identical.

**Functional suite.** The `Any`/`All` suites must stay green unmodified — that is the unification's regression
net. New tests land in a `NativeOwnedCollectionCountTests` class alongside them.

**Unit suite.** The five bare-`Any()` construction sites listed in §9 are expected, intended flips.
