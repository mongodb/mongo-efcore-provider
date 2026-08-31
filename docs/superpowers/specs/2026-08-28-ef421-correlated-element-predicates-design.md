# EF-421: correlated element predicates over an owned collection

## Problem

Two related shapes are currently declined by the native translator whenever an owned-collection
element predicate references the enclosing (outer) scope — e.g.:

```csharp
// Correlated filtered Count
Where(b => b.Posts.Count(p => p.AuthorName == b.Name) > 2)

// Correlated quantifiers
Where(b => b.Posts.Any(p => p.AuthorName == b.Name))
Where(b => b.Posts.All(p => p.AuthorName == b.Name))
```

`ReferencesEnclosingScope` (`MongoExpressionTranslator.MethodCalls.cs:238`) detects the
correlation and both call sites (`MongoExpressionTranslator.cs:590` for quantifiers, `:1051` for
`Count(pred)`) decline outright — falling back to driver-LINQ, which does translate these shapes
correctly today. This ticket makes both natively representable.

- `Count(pred)` is achievable with the **existing dialect**: a `$filter`'s `cond` can legally
  reference the enclosing document (`$$CURRENT`/document-root fields alongside `$$this`/element
  fields), so this only needs a genuine two-scope translator, mirroring the one
  `NativeSelectManyBinder` already has for reference/owned `SelectMany`.
- Quantifiers need a **different dialect**: `$elemMatch` cannot reference the enclosing document
  at all. The fix is a top-level `$expr` over `$anyElementTrue`/`$allElementsTrue` (over a `$map`
  that evaluates the element predicate), not `$elemMatch`.

Also in scope: relativizing `TryResolveOwnedFieldPath`/`TryResolveOwnedCollectionPath`, which
currently hard-decline outright in two-scope mode even for a hop that is correctly rooted in
whichever scope it belongs to.

## Scope

**In scope** — every construction site where a single-scope root translator is built from a
lambda whose parameter is directly available, so the correlation guard can be upgraded from
"decline" to "identify and build a two-scope child":

| Site | File:line | Operator |
|---|---|---|
| `NativeSlotPopulator.PopulateNativeSlots` | `NativeSlotPopulator.cs:47` (shared instance; each arm re-derives its own lambda at 77/85/96) | `Where`, `OrderBy`/`OrderByDescending`, `ThenBy`/`ThenByDescending` |
| `NativeProjectionBinder.TryPopulateNativeProjection` | `NativeProjectionBinder.cs:45` | `Select` projection leaf |
| `NativeCardinalityBinder.TryBindAggregate` | `NativeCardinalityBinder.cs:102` | scalar `All`/`Any(pred)`/`Count(pred)` aggregate forms |
| `NativeVectorSearchBinder` | `NativeVectorSearchBinder.cs:70` | `VectorSearch` preFilter |

**Out of scope:**
- `NativeGroupByBinder.TryBindGroupKey`/`TryBindGroupProjection` (`:57`, `:126`) — these only ever
  call `TryTranslateField` against a plain member access, never a nested predicate lambda; no
  element-scoped child translator is built from these paths, so there is nothing to upgrade.
- `NativeCardinalityBinder.TryBindReducer` — not implicated by the ticket's example shapes; a
  correlated predicate in reducer position (`First(b => ...)`) is a separate, unraised concern.
- Any correlation nested **two or more scopes deep** (e.g. a predicate inside a predicate inside
  a further owned quantifier, where the free parameter belongs to neither the immediate root nor
  the immediate element) — declines exactly as today. Only single-level correlation (element
  predicate referencing its immediate enclosing root) is handled.

## Design

### 1. Root-parameter capture (`_selfParam`)

`MongoExpressionTranslator`'s single-scope constructor gains an optional root parameter:

```csharp
public MongoExpressionTranslator(IEntityType entityType, ParameterExpression? selfParam = null)
```

Every in-scope call site (table above) passes its own lambda's `Parameters[0]` as `selfParam`.
`NativeSlotPopulator`'s shared instance is the one wrinkle: since one translator instance serves
multiple arms (`Where`/`OrderBy`/etc.), each arm sets `_selfParam` for the duration of its own
call rather than baking it in at the shared instance's construction — a small settable/scoped
field, not a constructor-only capture, is needed there specifically. (This is an internal
implementation choice for that one call site; it does not change the two-scope constructor.)

### 2. Identifying the correlation target

At the quantifier (`:590`) and `Count(pred)` (`:1051`) sites, `ReferencesEnclosingScope` already
finds *a* free parameter. Both sites are upgraded to also recover *which* parameter
(`FreeParameterVisitor` gains a way to report the found parameter, not just a bool), then check
`ReferenceEquals(found, translator._selfParam)`:

- **Match** → build a two-scope child translator (`new MongoExpressionTranslator(elementType,
  outerParam: found, outerEntityType: translator._entityType, innerPrefix: "")`) instead of
  declining. `innerPrefix` is empty because the element predicate stays inside `$filter`/`$map`,
  never unwound into the pipeline the way a `SelectMany` inner filter is.
- **No match** (correlation reaches past the immediate root — two-or-more-scopes-deep, or an
  unrelated captured parameter) → decline exactly as today. This is a strictly narrower guard
  than today's "any free parameter declines," so no previously-translating shape changes.

### 3. `MongoOuterFieldExpression` — outer-scope field reference

The two-scope translator's `TryResolveMember` currently (for `SelectMany`) resolves an
outer-rooted member by building an ordinary `MongoFieldExpression` prefixed to document root
(no prefix). Inside a `$filter`/`$map`'s `cond`, however, an unprefixed field path is ambiguous
with "the element's own field at document root" once rendered — the renderer needs to know this
field must resolve against `$$CURRENT` (document root), not `$$this` (the filter's element
variable), regardless of what wrapping construct it ends up inside.

New sealed node, `Expressions/MongoOuterFieldExpression.cs`:

```csharp
internal sealed class MongoOuterFieldExpression(IProperty Property, string ElementName) : MongoExpression;
```

Rendering (`MongoAggregationExpressionRenderer`): always `"$" + ElementName` — i.e. identical to
how a `MongoFieldExpression` renders **outside** any `$filter`/`$map` scope, regardless of how
deeply nested the render call currently is. It is never routed through whatever "current element
variable" state the renderer tracks for `$filter`/`$map` bodies.

Per the codebase's own stated invariant (a node needing different handling at 3+ existing sites
must be a sealed sibling type, not a bool flag on `MongoFieldExpression`), this becomes its own
node rather than an `IsOuter` flag: `IsQueryDialectRenderable` classification, aggregation
rendering, and `AllFieldsDefaultSerialized` recursion are the three sites that must each
explicitly account for it.

`IsQueryDialectRenderable` declines `MongoOuterFieldExpression` unconditionally (it only has
meaning inside an aggregation-expression `$filter`/`$map`/`$expr`, never a bare `$match`).
`AllFieldsDefaultSerialized` gets a `MongoOuterFieldExpression` arm identical to the existing
`MongoFieldExpression` arm (delegates to `NativeGroupByBinder.HasDefaultKeySerialization`).

The two-scope translator's `TryResolveMember` (member rooted on `_outerParam`) constructs
`MongoOuterFieldExpression` instead of `MongoFieldExpression` for the outer arm; the inner-scope
arm is unchanged (ordinary `MongoFieldExpression`, prefixed with `_innerPrefix` as today).

### 4. Correlated `Count(pred)` — two-scope `$filter`

No new stage/dialect: `MongoFilteredSizeExpression` already renders as a null-safe `$filter` over
the array with `cond` set to the translated element predicate
(`MongoAggregationExpressionRenderer`'s existing filtered-size rendering). The only change is that
`cond` may now contain a `MongoOuterFieldExpression`, which — per §3 — renders as a document-root
field reference. MongoDB's `$filter` `cond` already has access to variables outside its own `as`
scope (it is an ordinary aggregation expression evaluated with the surrounding document still in
scope via `$$CURRENT`/a bare `$fieldPath`), so no new stage type is needed — just correct
rendering of the outer field reference within it.

The correlated-guard removal at `:1051` (`ReferencesEnclosingScope(...) return null`) becomes the
match/no-match branch from §2.

### 5. Correlated quantifiers — `MongoQuantifierExpression` + `$anyElementTrue`/`$allElementsTrue`

New sealed node, `Expressions/MongoQuantifierExpression.cs`:

```csharp
internal sealed class MongoQuantifierExpression(
    MongoElementRefExpression ArrayPath, MongoExpression ElementPredicate, MongoQuantifierKind Kind)
    : MongoExpression;
```

Rendered (aggregation-expression dialect only — no query-dialect form, same bucket as
`MongoConditionalExpression`/`MongoDatePartExpression`) as:

```
{ $anyElementTrue: { $map: { input: {$ifNull: ["$ArrayPath", []]}, as: "this", in: <element predicate, $$this-relative> } } }
```

(`$allElementsTrue` for `Kind == All`). Unlike today's `$elemMatch`-based `All` — which needs the
exact-complement negation trick because `$elemMatch` has no "for all" form — `$allElementsTrue` is
a native MQL operator, so `All`'s translation needs **no negation**: the element predicate
translates directly, the same way `Any`'s does. This also sidesteps `MongoExpressionNegator`
entirely for the correlated path (negation is still used for the *uncorrelated* `All` path,
unchanged).

Because this node has no query-dialect form, `IsQueryDialectRenderable` returns `false` for it,
and the existing top-level "wrap the non-dialect boolean subtree in `$expr`" fallback in
`MongoQueryLanguageRenderer.RenderNode` (the same mechanism `Not`-over-non-dialect uses per
EF-396) wraps it automatically — no new `$expr`-wrapping plumbing needed at that layer.

At the quantifier call site (`:590`), on a **correlation match** (§2): build the two-scope element
translator, translate the predicate body directly (no negation for `All`), gate on
`MongoAggregationExpressionRenderer`-renderability of the result (mirroring the existing
`IsQueryDialectRenderable` gate, but for the aggregation dialect this time, since this whole node
only ever renders there), and return `MongoQuantifierExpression`. The **uncorrelated** path
(today's `$elemMatch`-based translation) is unchanged — this is a new arm alongside it, not a
replacement.

### 6. Relativizing `TryResolveOwnedFieldPath`/`TryResolveOwnedCollectionPath`

Both currently decline unconditionally when `_outerParam is not null` (i.e. in any two-scope
translator, regardless of which scope the hop's root actually belongs to). Per the EF-424
precedent (Query/AGENTS.md), the fix is to resolve the hop's **root parameter identity** first:

- Root is `_outerParam` → resolve against `_outerEntityType`, path relative to document root,
  wrapped as `MongoOuterFieldExpression`/an outer-relative array path (needed for a correlated
  reference like `p.OtherOwned.Value == b.Owned.Value`, where `b.Owned` is an outer-scope owned
  hop).
- Root is the two-scope translator's own inner root parameter (the element parameter for
  `Count(pred)`, or whatever `SelectMany`/quantifier scope already threads through) → unchanged
  existing relative-path behavior.

This removes the blanket two-scope decline in favor of the same identity-based routing used
throughout this design (never by name — see the codebase's own "scope must be resolved by
parameter identity" invariant).

### 7. `MongoFieldPrefixRewriter` (or equivalent path-prefixing pass, if any exists downstream)

If a prefix rewrite pass exists that walks a translated tree adding a path prefix (used e.g. for
`SelectMany`'s inner-filter unwind path), it must pass `MongoOuterFieldExpression` through
**unprefixed** — an outer field is never relative to the inner unwind scope. (Confirm during
implementation whether this pass actually touches quantifier/`Count(pred)` trees at all today —
if it doesn't, this is a non-issue and can be dropped from the plan.)

## Testing

- `NativeOwnedCollectionAllTests`/`NativeOwnedCollectionCountTests` (and the sibling `Any`
  tests, if separately named) move their "declines on correlation" cases to "translates," with
  the correlated shapes added as differential-correctness `[Theory]` cases against an in-memory
  LINQ oracle evaluated over the same `Expression` — the existing pattern for this feature family
  — covering: ragged/missing/empty arrays, a `null` outer field, a correlated `Count(pred)` used
  both as a predicate (`Where(...)`) and inside `NativeCardinalityBinder`'s scalar-aggregate
  position, and both `Any`/`All` quantifiers.
- New unit tests for `MongoOuterFieldExpression`/`MongoQuantifierExpression` rendering in
  isolation (`MongoAggregationExpressionRendererTests` or equivalent).
- A `NativeOnly`-mode assertion for each newly-translating shape (per this codebase's own
  "MQL shape cannot prove a query went native" pitfall) — assert success under `NativeOnly`,
  not just a plausible-looking MQL string under `Native`.
- Confirm the **uncorrelated** `Any`/`All`/`Count(pred)` paths are unaffected (no MQL/behavior
  change) — regression-only assertions, no new cases needed since those paths are untouched code.
- Multi-EF-version: no `#if` expected — this is pure aggregation-pipeline generation, unaffected
  by EF8/9/10 API differences (confirm during implementation).

## Risks / invariants this design must preserve

- **Scope by identity, never by name** — the `ReferenceEquals(found, _selfParam)` check (§2) is
  the load-bearing guard; a name-based shortcut anywhere in this feature would reopen the
  by-name-retargeting hazard this codebase has already paid for more than once.
- **Decline gracefully, never approximate** — a correlation that doesn't match `_selfParam`
  (nested two-or-more scopes deep) must continue to decline outright, not attempt a partial or
  best-effort translation.
- **`$expr` never leaks into `$elemMatch`** — `MongoQuantifierExpression` and
  `MongoOuterFieldExpression` must both be classified as aggregation-only by
  `IsQueryDialectRenderable`, or a future caller could nest one inside `$elemMatch` and produce a
  hard server error rather than a decline.
- **`Count(pred)`'s existing "no aggregation-renderability gate" note** (`:1058`-`:1093` remarks)
  — that deliberate leniency (admit even when the aggregation renderer can't express the
  predicate, because the render-time throw is caught and degrades gracefully) must be preserved
  for the *uncorrelated* path; the new correlated path adds its own explicit renderability gate
  (§5) because correlated quantifiers have no equivalent graceful degrade — confirm this asymmetry
  is intentional during implementation and don't accidentally harmonize the two.
