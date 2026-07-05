# Native query — predicate breadth (`$expr` renderer + core operator tranche) — design

**Date:** 2026-07-04 · **Branch:** `EF-329` (off `EF-323-impl` / SP1) · **JIRA:** EF-329
**Program:** `2026-06-23-native-query-provider-design.md` (epic EF-322) · **Builds on:** SP1 `2026-06-20-mongo-query-ast-foundation-design.md` (EF-323)

> Sub-project 2 of the native LINQ query provider rebuild. Broadens the native translator's predicate
> support beyond the SP1 parity set (simple filter/sort/paging), and stands up the aggregation-expression
> (`$expr`) renderer behind the seam SP1 stubbed.

## TL;DR

- Add a second BSON **dialect** — aggregation expressions (`$expr`) — alongside the existing query/match
  dialect, so predicates the match dialect can't express (field-to-field, arithmetic, computed operands)
  become natively representable instead of falling back to driver-LINQ.
- **Index-first (the governing principle).** Query-dialect filters can use indexes; `$expr` generally
  cannot. So we **try really hard to keep indexes usable**: always prefer a semantically-correct
  query-dialect rendering, and drop to `$expr` only for the *smallest* subtree that has no such rendering.
- **Dialect strategy = per-clause mixing (approach B).** `&&`/`||` structure stays at the query level so
  every branch is independently indexable; `$expr` is pushed as deep as possible and wraps only the
  minimal non-query-expressible subtree.
- **Scope = infra + a core operator tranche.** This spec ships the `$expr` renderer machinery plus a
  first tranche of high-value operators. The *computed* long tail (string transforms, date parts,
  `Math.*`, type-changing casts) is a follow-on ticket that builds on the infra shipped here.
- Zero regressions, no `NativeOnly` coverage shrink — driver-LINQ stays the gated fallback behind
  everything, exactly as in SP1.

## Background: where SP1 left the seam

SP1 (EF-323) renders predicates in exactly one dialect — the **query/match** dialect
(`{ Age: { $gt: 21 } }`, `$and`/`$or`, bare-bool, `$ne`) — via `MongoQueryLanguageRenderer`.
`MongoExpressionTranslator` produces a **dialect-neutral** `MongoExpression` tree
(`MongoFieldExpression` / `MongoConstantExpression` / `MongoParameterExpression` / `MongoBinaryExpression`
/ `MongoUnaryExpression`), and conservatively returns `false` for anything outside the parity set —
nullable equality, numeric casts, method calls, computed operands, field-to-field comparisons — so those
queries fall back to driver-LINQ (correct results; throws under `NativeOnly`).

The program design reserved an **`$expr` (aggregation-expression) renderer** as a stubbed seam and named
predicate breadth as the largest `NativeOnly`-mode coverage bucket. This sub-project fills that seam.

## Governing principle: index-first

Query-dialect filters (`{field: …}`, `$regex`, `$in`) are eligible to use collection indexes; an `$expr`
predicate generally is not. Every dialect decision in this sub-project is subordinate to one rule:

> **A node is *query-native* iff *any* semantically-correct query-dialect rendering of it exists, and
> that rendering is always preferred. `$expr` is used only when no correct query-dialect rendering
> exists.**

Concretely, several operators have both forms; we must choose the indexable one:

| Predicate | Query dialect (preferred, indexable) | `$expr` (only if forced) |
|---|---|---|
| `x.Name.StartsWith("A")` | `{ Name: { $regex: "^A" } }` (anchored ⇒ index-usable) | `{ $regexMatch: … }` |
| `list.Contains(x.Id)` | `{ Id: { $in: [ … ] } }` | `{ $in: [ "$Id", … ] }` |
| `x.NullableInt == 5` | `{ N: 5 }` | — |
| `x.A == x.B` (field↔field) | *(none)* | `{ $expr: { $eq: [ "$A", "$B" ] } }` |
| `x.A + x.B > 5` | *(none)* | `{ $expr: { $gt: [ { $add: [ "$A", "$B" ] }, 5 ] } }` |

## Target architecture

### Two renderers, one boundary

Every predicate still renders to a **query-language `BsonDocument`** — because `$expr` is itself a query
operator usable anywhere a filter is, including inside `$and` / `$or` arrays. The renderer's job is to emit
query dialect wherever possible and wrap only non-query-expressible subtrees in `{ $expr: … }`.

- **`MongoQueryLanguageRenderer`** (existing) — owns the **query dialect**. Extended in this sub-project
  with `$regex` (from string prefix/suffix/substring), `$in`/`$nin` (from collection `Contains`), and
  correct nullable-equality / `== null` rendering.
- **`MongoAggregationExpressionRenderer`** (**new**) — owns **aggregation expressions**
  (`{ $eq: [ … ] }`, `{ $gt: [ … ] }`, `{ $add | $subtract | $multiply | $divide | $mod: [ … ] }`, field
  refs as `"$path"`). Produces the *inner* expression; the query renderer wraps it as `{ $expr: … }` when
  it stitches the subtree back into the query-dialect document.

### The boundary rule (per-clause mixing, index-first)

At each `MongoExpression` node the renderer asks: *is this subtree query-native?* (does a correct
query-dialect rendering exist for it and all descendants?).

- **`&&` / `||`:** render **each child independently** at the query level — each child is either a plain
  query fragment or an `{ $expr: … }`-wrapped fragment — then combine with the existing
  `CombineAnd` / `CombineOr` (which already flatten and merge). The logical structure therefore stays at
  the query level and every branch remains independently indexable.
- **A comparison / leaf** is query-native iff it has a query-dialect form per the table above
  (field-vs-constant, field-vs-parameter, bare bool, nullable equality, `$regex`, `$in`). If it is not
  (field↔field, arithmetic/computed operand), *that node* — and only that node — is rendered by the
  aggregation-expression renderer and wrapped once in `{ $expr: … }`.

This yields:
- `Name.StartsWith("A") && Age > 20` → `{ $and: [ { Name: { $regex: "^A" } }, { Age: { $gt: 20 } } ] }`
  (both branches indexable).
- `A + B > 5 && Age > 20` →
  `{ $and: [ { $expr: { $gt: [ { $add: [ "$A", "$B" ] }, 5 ] } }, { Age: { $gt: 20 } } ] }`
  (`$expr` scoped to the one computed branch; `Age` still indexable).

### Documented deviation from the program design

The program design and `Query/AGENTS.md` state "dialect choice is made in the lowerer." With per-clause
mixing the dialect boundary is **per-subtree** and can only be computed while rendering, so **the renderer
owns the dialect decision**, not the lowerer. The `MongoExpression` nodes remain **dialect-neutral** — the
design's actual invariant is preserved. `MongoSelectLowerer` still produces a single `MongoMatchStage`
carrying the dialect-neutral predicate; the renderer chooses per-subtree dialect. This refinement is
recorded here and `Query/AGENTS.md` is updated accordingly.

## Operator tranche

### Included in this sub-project

**Query-dialect / indexable (primary coverage payoff):**

1. **Nullable equality / inequality**, including `== null` and `!= null`. SP1 rejects these
   (`property.IsNullable` guard in `TranslateComparison` / bare-bool / `Not`). Render in query dialect with
   driver-matching null/missing semantics (`{ N: null }` matches both null and missing, per driver-LINQ).
   Match the driver's existing MQL for these shapes so results and pipeline agree.
2. **`Contains` on a collection → `$in` / `$nin`.** Both an inline literal collection (baked at compile
   time) and a captured/parameterized collection (bound per execution — see binding below).
   `!list.Contains(x)` → `$nin`.
3. **`string.StartsWith` / `EndsWith` / `Contains` → `$regex`.** Anchored (`^…`) for `StartsWith` so the
   index is usable; `…$` for `EndsWith`; unanchored for substring `Contains`. Escape the pattern and match
   the driver's case/culture handling (ordinal). Constant and parameterized search terms.

**`$expr` infrastructure, proven end-to-end on its minimum real consumers:**

4. **Field-to-field comparisons** (`x.A == x.B`, `<`, `<=`, `>`, `>=`, `!=`) and
   **arithmetic-in-comparison** (`+`, `-`, `*`, `/`, `%` on an operand of a comparison). These are the
   smallest predicates that genuinely require `$expr`, so the seam ships exercised rather than stubbed.

### Deferred to a follow-on ticket (computed long tail)

All `$expr`-only, all building cleanly on the renderer shipped here:
`string.Length`, `ToUpper` / `ToLower` / `Substring` / `Trim` / `IsNullOrEmpty`, date parts
(`.Year` / `.Month` / `.Day` / …), `Math.*` functions, and member casts that change type
(the `HasNumericConvert` fallback in SP1).

## Parameter binding (B2, cross-dialect)

The existing `PlaceholderTable` / B2 machinery is dialect-agnostic: both renderers emit placeholder
sentinels identically, and `MongoPipelineFactory.Build` substitutes them per execution through the recorded
serializer. Reused unchanged for scalar operands in both dialects.

One addition: **collection-membership parameters.** `$in` / `$nin` bind a *whole* captured `IEnumerable`
to a single placeholder; at `Build` time it serializes **element-wise** through the property's serializer
into a `BsonArray`. (Inline literal collections are baked into the template at compile time, as with other
constants.)

## Fallback & gate — unchanged

Driver-LINQ remains the gated fallback behind everything. The QMTEV catch-all still marks any operator it
does not lower into a slot as `IsNativeRepresentable = false`. Within the `Where` slot, the broadened
`MongoExpressionTranslator` now accepts the tranche above; anything still outside it returns `false` and
falls back exactly as today. No change to the compile-time gate, streaming eligibility, or the
`NativeOnly` throw contract.

## Testing

- **Unit tests** (`tests/…/UnitTests/Query/NativeTranslation/`): per operator, assert the exact rendered
  MQL — both the query-dialect form and, where relevant, the `$expr` form — plus:
  - the query/`$expr` **boundary placement** (that `$expr` wraps only the minimal subtree), and
  - **cross-dialect AND/OR mixing** (query branch + `$expr` branch combined correctly), and
  - **index-first choices** (StartsWith ⇒ anchored `$regex`, Contains ⇒ `$in`, not their `$expr` forms).
  - Cross-dialect **parameter binding** correctness across repeated executions (including `$in` arrays).
- **`NativeOnly`-mode assertions**: prove each new operator genuinely goes native (per `Query/AGENTS.md`,
  `NativeOnly` success/throw is the only reliable native-vs-fallback signal — MQL shape alone can't prove
  it for shapes the fallback renders identically).
- **Specification suite**: newly-native operators change their MQL (moving off driver-LINQ), so their
  overridden `AssertMql` expectations are updated. Acceptance: full FunctionalTests + SpecificationTests
  green in `Native` mode on EF10 and EF8; net `NativeOnly` spec coverage **grows**, none shrinks.

## Acceptance criteria

1. Zero regressions: full FunctionalTests + SpecificationTests green in `Native` mode on EF10 and EF8.
2. `NativeOnly`-mode native coverage grows vs SP1 by the tranche above; nothing that was native in SP1
   becomes non-native.
3. Each in-scope operator has unit tests asserting rendered MQL (query and/or `$expr` dialect), boundary
   placement, index-first choice, and parameterized-binding correctness.
4. `Query/AGENTS.md` updated for the dual-dialect renderer and the renderer-owns-dialect refinement.
5. Indexes remain usable for every predicate that has a query-dialect form (verified by asserting the
   query-dialect MQL, not `$expr`, for those shapes).

## Out of scope (later sub-projects, unchanged from the program plan)

Projection pushdown (`$project`, SP3), scalar cardinality (SP4), collection Includes (SP5), remaining
operators — `GroupBy` / `SelectMany` / set ops / `Distinct` / `OfType` / `VectorSearch` / non-canonical
paging (SP6), one-pass materializer (SP7). The computed-operator long tail (see above) is its own follow-on
within the predicate-breadth theme.
