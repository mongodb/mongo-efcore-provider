# Native owned-collection sub-property predicates (`$elemMatch`) — design

*Epic EF-322 (native LINQ query provider). Owned-data translator slice, following the owned single-reference
sub-property slice.*
*Branch `EF-322-owned-collection-predicates-native`, stacked on the native tip `2a9b56e`
(`origin/NativeQueryOngoing`).*
*A JIRA number should be filed; this doc will be updated with it.*

---

## 1. Problem

A predicate that quantifies over an **owned (embedded) collection** navigation —
`ctx.Blogs.Where(b => b.Posts.Any(p => p.Title == "x"))`, `Where(b => b.Posts.Any())` — currently
**falls back to driver-LINQ**, even under `Native` mode. For a document database this is a
bread-and-butter shape: an embedded array is the idiomatic MongoDB modelling pattern, and `$elemMatch`
is its idiomatic, index-usable query operator.

The preceding slice (`2a9b56e`) made owned **single-reference** sub-property dotted paths native and named
this shape as its explicit deferral (`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md:71`):

> **Deferred (out of scope for this slice):** an owned-COLLECTION sub-property access
> (`Where(e => e.Posts.Any(p => p.Title == ...))`, an array-projection of a collection sub-property — the
> intermediate-navigation guard above declines any collection hop, so these still fall back)

**Root cause — two decline sites, one missing capability.**

1. `Where(b => b.Posts.Any(pred))` never reaches path resolution at all: the chain walk
   (`MongoExpressionTranslator.TryGetMemberOrEFProperty`,
   `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:639-660`)
   accepts only a `MemberExpression` hop or an `EF.Property(root, "Name")` call, so a quantifier method call
   terminates the walk, the collected name list stays empty, and `TryResolveOwnedFieldPath` declines at
   `:601-602` ("root is not the query parameter").
2. `Where(b => b.Posts.Count > 2)` declines one step further in, at the intermediate-hop guard
   (`MongoExpressionTranslator.cs:611-617`), which rejects any `IsCollection` hop.

Both are clean declines — `NativeSlotPopulator.cs:81-91` marks the select not-natively-representable and the
query routes to driver-LINQ (throwing only under `NativeOnly`). No wrong `$match` is emitted today.

**The missing capability.** There is **no array-operator support anywhere in the native layer**. A whole-tree
grep for `$elemMatch`, `$all`, `$size` finds exactly one hit:
`NativeTranslation/MongoAggregationExpressionRenderer.cs:51` (`$size` in the *aggregation* dialect, emitted
only by `NativeProjectionBinder.cs:222` over a `$lookup` alias for a projected **reference**-collection
`.Count`). No `$elemMatch`, no query-dialect `$size`, no occurrence of `elemMatch` anywhere in `src/` or
`tests/`.

**What already exists and can be reused.** Owned-collection *element* predicates are already translated for
`SelectMany`: `NativeSelectManyBinder.TryBuildOwnedInnerFilter` (`NativeSelectManyBinder.cs:756-795`) builds
a **single-scope translator on the owned element entity type** and then blanket-prefixes the result with the
unwind path via `MongoFieldPrefixRewriter.Rewrite` (`:788`). `$elemMatch` needs precisely the first half of
that — the element-scoped translation — and specifically must *not* prefix, because inside `$elemMatch`
field paths are element-relative. The two are mirror images.

## 2. Goal & success criteria

**Goal.** Make `Any` quantifiers over owned (embedded) collection navigations go native, emitting the
index-usable `$elemMatch` query dialect.

**In scope.**
- `b.Posts.Any(p => <element predicate>)` → `{ "<arrayPath>": { $elemMatch: <element-relative predicate> } }`
- `b.Posts.Any()` → `{ "<arrayPath>.0": { $exists: true } }`
- Negation of both (`!b.Posts.Any(...)`) → `$not`-wrapped `$elemMatch` / `$exists: false`
- Nested `Any`-within-`Any` (`b.Fams.Any(f => f.Children.Any(c => c.Age > 3))`) → nested `$elemMatch`
- The collection may be reached through owned single-reference hops (`b.Home.Posts.Any(...)`)
- The element predicate may use the full query-dialect operand set the shared translator already supports:
  equality, `!=`, relational, `== null` / `!= null`, `&&` / `||`, bare-bool, `Contains` (`$in`),
  `StartsWith`/`EndsWith`/`Contains` (`$regularExpression`)

**Out of scope for this slice** — see §7. Notably `All`, `.Count`, and projections.

**Success bar.**
- The shapes go native (succeed under `MongoQueryMode.NativeOnly`).
- **`Native` results equal `DriverLinq` results** (there is a driver-LINQ oracle for every shape here) across:
  populated array, **empty** array, **missing** array field, single- and multi-condition element predicates,
  negated forms, nested `Any`, and the reached-through-owned-ref form.
- Multi-condition element predicates must require **one element to satisfy all conditions** — the semantics
  that the dotted-path alternative would get silently wrong (§3, approach B).
- No-tracking **and** tracked both correct.
- Zero regressions across EF8 / EF9 / EF10.

**This changes the native eligibility set** (new shapes go native) — see §6.

## 3. Approach

**A — new `$elemMatch` AST node (chosen).** Add
`Query/Expressions/MongoElemMatchExpression.cs`: `(string ArrayPath, MongoExpression ElementPredicate,
bool Negated)`. Three touch points:

1. **Translator** — a new `MethodCallExpression` case in `MongoExpressionTranslator.TranslateNode`, beside
   the existing `TryMatchContainsMethod` (`:265-276`) and `TryMatchRegexMethod` (`:280-294`) cases. It
   resolves the array path (§4), builds an element-scoped child translator, translates the lambda body,
   validates the result is query-dialect-renderable (§5), and returns the node. The existing `Not` case
   (`:244-262`) gains a `Negated`-flip branch, exactly as it already does for `MongoInExpression` and
   `MongoRegexExpression` — necessary because `MongoQueryLanguageRenderer.RenderUnary` (`:135-150`) supports
   `Not` only over a bare field and throws otherwise.
2. **Renderer** — one new case in `MongoQueryLanguageRenderer.RenderNode` (`:74-88`), *before* the
   `_ => RenderAsExpr` catch-all (`:100-101`), which would otherwise silently route the node into the
   `$expr` aggregation dialect.
3. **`MongoFieldPrefixRewriter`** — a case that prefixes `ArrayPath` and leaves `ElementPredicate`
   **untouched**. This is correctness-critical *and* crash-critical: the rewriter's `_ => throw`
   (`MongoFieldPrefixRewriter.cs:47-48`) means an `$elemMatch` reaching it without a case would throw
   instead of translating, and blindly prefixing the element-relative child would silently mis-address every
   field inside the `$elemMatch`.

   **Spike-confirmed load-bearing today, not defensive — and therefore atomic with touch point 1.** The
   probe `SelectMany(b => b.Posts.Where(p => p.Comments.Any(c => c.Text == "t")), (b, p) => p.Heading)`
   reaches `NativeSelectManyBinder.TryBuildOwnedInnerFilter`, whose element-scoped translator is built on the
   **non-root** `Post` type and whose existing `MongoFieldPrefixRewriter.Rewrite(expr, unwindPath)` call then
   prefixes with `"Posts"`. Today that shape hard-fails in *every* mode (the inner translator can't handle
   `Any`, so the binder declines and `TranslateSelectMany` returns null). Once the translator emits
   `$elemMatch`, this shape starts **working** — `ArrayPath` `"Comments"` becomes `"Posts.Comments"`, which
   correctly addresses the unwound element. But shipping the translator case *without* the rewriter case
   would replace today's clean decline with a **crash thrown from inside pre-existing, unrelated code**. The
   two must land together; the implementation order is rewriter-first (touch point 3 before touch point 1).

**B — dotted-path rewrite, no new node (rejected).** `{ "Posts.Title": "x" }` already means "some element
has `Title == "x"`", so the element-scoped translation could simply be prefixed with the array path via the
existing `MongoFieldPrefixRewriter` and emitted through the existing renderer — zero new nodes. Rejected:
this is only equivalent for a **single-condition** element predicate. With two conditions the dotted form
permits them to match *different* elements, so `Any(p => p.A == 1 && p.B == 2)` would return **silently
wrong rows** unless a condition-counting guard is exactly right. Trading a new AST node for a
silent-wrong-data guard is the wrong trade.

**C — pre-rewrite the quantifier above the translator (rejected).** Adds a layer without removing any work;
the translator still needs to resolve the path and scope the element predicate.

### Rendered forms

| LINQ | MQL |
|---|---|
| `b.Posts.Any(p => p.Title == "x")` | `{ "Posts": { $elemMatch: { "Title": "x" } } }` |
| `b.Posts.Any(p => p.A == 1 && p.B > 2)` | `{ "Posts": { $elemMatch: { "A": 1, "B": { $gt: 2 } } } }` |
| `!b.Posts.Any(p => p.Title == "x")` | `{ "Posts": { $not: { $elemMatch: { "Title": "x" } } } }` |
| `b.Posts.Any()` | `{ "Posts.0": { $exists: true } }` |
| `!b.Posts.Any()` | `{ "Posts.0": { $exists: false } }` |
| `b.Fams.Any(f => f.Children.Any(c => c.Age > 3))` | `{ "Fams": { $elemMatch: { "Children": { $elemMatch: { "Age": { $gt: 3 } } } } } }` |

`Any()` uses the `"<path>.0": { $exists: … }` form rather than `$size`/`$ne: []`: it is index-usable, and it
is correct for **both** an empty array and a missing field. `{ Posts: { $ne: [] } }` is *not* — a missing
field is not equal to `[]`, so it would match, whereas LINQ `Any()` over a missing embedded array (which EF
materializes as an empty list) is `false`. The `$elemMatch: {}` spelling is left to the spike to confirm or
reject (§8); the `.0`/`$exists` form is the design's choice regardless.

`$not`-wrapped `$elemMatch` is the correct negation *including* the empty/missing cases: no element matches,
so `!Any(...)` is `true`, and `$not: { $elemMatch: … }` matches such documents.

## 4. Path resolution — the load-bearing decision

A new private helper `TryResolveOwnedCollectionPath` beside the landed `TryResolveOwnedFieldPath`. It reuses
the same hop walk (`TryGetMemberOrEFProperty`, so member-or-`EF.Property` hops are both accepted, as
required by the previous slice's finding that EF nav-expansion rewrites member chains into `EF.Property`
calls), and:

1. walks the quantifier's *source* expression outer-to-inner, collecting hop names;
2. requires the root to be the query parameter;
3. requires every **non-final** hop to resolve to an `IsEmbedded() && !IsCollection` navigation;
4. requires the **final** hop to resolve to an `IsEmbedded() && IsCollection` navigation;
5. builds the array path by joining the hop element names **relative to the translator's current scope
   entity type** — each hop's element name being `navigation.TargetEntityType.GetContainingElementName()`,
   the same helper the streaming rewriter and the shapers use, so the emitted path is guaranteed to match
   stored layout (including a `HasElementName`-overridden or shared-type owned navigation).

**Why relative, not `GetDocumentPath()` — this is the C1 lesson applied.** The previous slice shipped a
critical fix (`0c36ac8`) because `TryResolveOwnedFieldPath` built paths with `GetDocumentPath()`, which is
absolute-from-the-document-root; when the translator was constructed on a non-root scope (the owned
`SelectMany` inner-filter translator, whose *caller* prefixes separately) the result was double-prefixed and
silently matched nothing. The fix was a blanket `if (!_entityType.IsDocumentRoot()) return false;`
(`MongoExpressionTranslator.cs:589-590`).

Building the array path from **scope-relative** hop names makes that guard unnecessary here, because a
scope-relative path composes correctly with `MongoFieldPrefixRewriter` prepending instead of fighting it.
At the document root, relative-join and `GetDocumentPath()` produce the same string; below the root, only
relative-join is correct. Two consequences fall out:

- **Nested `Any`-within-`Any` works for free.** The element-scoped child translator resolves `Children`
  relative to the element type, which is exactly what the enclosing `$elemMatch` needs.
- **An `$elemMatch` inside a `SelectMany` scope stays correct**, provided the `MongoFieldPrefixRewriter` case
  from §3 is in place.

The **single-scope guard is kept** (`_outerParam is null && _innerPrefix is null`): a two-scope
(cross-scope `SelectMany`) quantifier is declined, consistent with the landed slice.

### The element-scoped child translator

`new MongoExpressionTranslator(elementEntityType)` — the one-argument constructor, no outer param, no
prefix. Field paths come out element-relative, which is what `$elemMatch` requires.

For **one specific out-of-scope shape** this needs no extra guard code: `Any(p => p.Title == "x")` resolves
through `TryResolveMember`'s bare-parameter fast path, while `Any(p => p.Sub.City == "x")` — a scalar leaf
reached through an owned reference *inside* the element — declines cleanly, because the child translator's
scope is not a document root and the landed `IsDocumentRoot` guard fires.

**That is NOT a general safety argument for the element-scoped translator, and this paragraph must not be read
as one.** The element-scoped translator is *single-scope*, and single-scope `TryResolveMember` resolves a
member by **name** with no parameter-identity check. So an element predicate that references the **enclosing**
entity (`Any(i => o.Name == "x")`) is *not* self-enforcing at all: when both types declare the same property
name it silently resolves against the element and returns wrong rows. That shape needs — and, as built, has —
an explicit guard (`ReferencesEnclosingScope`, §7). The `IsDocumentRoot` interaction above covers only the
nested-owned-*scalar*-leaf case it examines.

That is asymmetric with collection hops (a collection hop through an owned ref inside an element *is*
allowed, a scalar leaf through one is not), and the asymmetry is deliberate for this slice. It also points at
a clean follow-on: relativizing `TryResolveOwnedFieldPath` the same way would make nested owned scalar leaves
work in non-root scopes and would supersede the blanket `IsDocumentRoot` decline with something more
surgical (§7).

## 5. Query-dialect enforcement

Inside `$elemMatch` the child must be **query dialect**, and the renderer's catch-all would otherwise wrap a
non-query-dialect child in `$expr` silently (`MongoQueryLanguageRenderer.cs:100-101`).

**The classifier is a correctness gate, not an optimisation (spike-confirmed, upgraded from the original
framing).** `$expr` inside `$elemMatch` is not merely non-indexable — it is a **hard server error**:

```
{ Posts: { $elemMatch: { $expr: { $gt: ['$Rank', 0] } } } }
→ MongoCommandException: Command find failed: $expr can only be applied to the top-level document.
```

So an element predicate the classifier fails to catch would make the whole query **throw at execution
time**, under `Native` as well as `NativeOnly` — not merely run slowly. The classifier must therefore run
before any `MongoElemMatchExpression` is constructed, and the comment pairing it with `RenderNode` must say
this explicitly rather than motivating it by indexing alone.

A new static classifier — `IsQueryDialectRenderable(MongoExpression)` — is added **immediately beside**
`RenderNode`, mirroring its query-dialect cases one-for-one (`AndAlso`/`OrElse`, `IsQueryNativeComparison`
comparisons, `Not`-over-bare-field, bare field, `In`, `Regex`, and now `ElemMatch`), with a comment on each
side stating that the two must change together and a unit test pinning the pairing.

The **translator** runs the classifier and declines (`return null`) when the element predicate has no
query-dialect form, so the decline happens at translate time → `Route = Fallback` → driver-LINQ (throwing
only under `NativeOnly`). Consequence: `Any(p => p.Views > p.Likes)` (field-to-field) and arithmetic element
predicates keep falling back. This is the index-first dialect rule the project already follows — `$expr` is
the last resort, and an `$elemMatch` whose child is non-indexable defeats the point of emitting `$elemMatch`
at all.

**No converter / `BsonRepresentation` guard is needed on the element predicate.** The previous slice's spike
(finding D1) established that the *predicate* side serializes the compared value through the leaf property's
serializer, so a value converter or non-default representation is applied on the query side and results match
driver-LINQ; only the *projection* side needed a guard. The `$elemMatch` child is a predicate and uses the
same `MongoValueRenderer` path, so it inherits that property. The spike re-confirms it empirically for an
element-scoped leaf (§8).

## 6. This changes the eligibility set — handling the flips

New shapes go native, so the `NativeOnly` pass-set may change. Per the provider's versioning rubric this is
**not** a breaking change: results are unchanged, the fallback still exists for everything not admitted, and
which internal path a supported query takes (plus the emitted MQL) is explicitly non-contract.

Expected flips:

- **Spec suite: zero.** Northwind has no owned collections. To be confirmed by the `NativeOnly` EF10 sweep
  (target: identical to the recorded baseline).
- **Functional: silent path flips in `OwnedEntityTests`** — roughly ten result-only cases exercise exactly
  these shapes today via driver-LINQ: `:160` and `:173` (`p.locations.Any(l => l == location)` and its
  negation), `:1040` (`OwnedEntity_collection_can_be_queried_on`), `:1052`
  (`OwnedEntity_nested_one_level_allows_list_nested_where`), and the nested chains at `:1138`, `:1254`,
  `:1257`, `:1260`, `:1287`, `:1308`. They assert results only, so they pass either way — they are the
  **behavioral oracle** for this slice and stay as they are. Note that `:160`/`:173` compare a **whole
  element** (`l == location`), which this slice declines (§7), so those two should *not* flip; the spike
  confirms which actually do.
- **No test pins the current fallback** for these shapes, so no assertion inversions are expected. The
  routing proof they lack is added by the new tests (§9).
- **One emergent new capability, spike-confirmed:** an owned `SelectMany` whose inner `Where` predicate is
  itself an owned-sub-collection `Any` —
  `SelectMany(b => b.Posts.Where(p => p.Comments.Any(c => c.Text == "t")), (b, p) => p.Heading)` — currently
  **hard-fails in every mode** (no driver-LINQ oracle). It starts working natively once this slice lands, via
  the `MongoFieldPrefixRewriter` case composing `"Comments"` into `"Posts.Comments"` after the `$unwind`.
  This is a strict improvement (throw → correct data), but it has no oracle, so it needs a test with
  hand-computed expected values.

## 7. Non-goals (deferred — separate slices)

- **`All(pred)`** → `{ path: { $not: { $elemMatch: <negated pred> } } }`. Correct in principle (both LINQ
  `All` and `$not`/`$elemMatch` are true for empty/missing arrays), but it requires **negating an arbitrary
  element predicate**, and the renderer already has a known `Not`-over-unsupported-subtree gap. A
  mis-negated predicate returns wrong rows rather than declining — highest silent-wrong-data risk of the
  candidate shapes, so it gets its own slice.
- **`.Count` in a predicate** (`Where(b => b.Posts.Count > 2)`) — a different decline site
  (`MongoExpressionTranslator.cs:611-617`) and a second dialect decision (query-dialect `$size`, which
  supports only exact size, vs. the `"path.n": {$exists: true}` index trick, vs. `$expr` + `$size`).
- **Projections** — `Select(b => b.Posts.Count)` over an *embedded* collection (today's `$size` support is
  reference-collection/`$lookup`-alias only, `NativeProjectionBinder.cs:172-224`) and array projections
  `Select(b => b.Posts.Select(p => p.Title))`.
- **Primitive-element collections** (`List<string> tags`) and **whole-element equality**
  (`p.locations.Any(l => l == someLocation)`) stay out of scope — but the spike found the **mechanism is not
  a same-shape decline**: EF Core's own `AllAnyToContainsRewritingExpressionVisitor` rewrites
  `Any(x => x == c)` into `Contains(c)` *before* the native translator sees it, so no `Any` node ever reaches
  the new matcher for these shapes. They are handled (or declined) by the pre-existing `Contains`/`$in` path,
  entirely orthogonal to this slice. Tests for them must assert whatever that path actually does, not assume
  a decline.
- **Non-query-dialect element predicates** (field-to-field, arithmetic) — declined by §5.
- **Two-scope (cross-scope `SelectMany`) quantifiers** — declined by the single-scope guard (§4).
- **A correlated element predicate** (one referencing the enclosing entity, e.g.
  `Where(o => o.Items.Any(i => o.Name == "x"))`, including a mixed conjunct `Any(i => o.Name == "x" && i.Rank > 1)`
  and a bare enclosing bool `Any(i => o.Flag)`). Declined by a dedicated guard,
  `MongoExpressionTranslator.ReferencesEnclosingScope` — added as a whole-branch-review fix, because without it
  the element-scoped (single-scope, name-only) translator silently retargets the enclosing condition at the
  element whenever the two types share a property name, returning wrong rows. Supporting it is a follow-on slice
  and needs more than a two-scope translator: `$elemMatch` cannot reference the enclosing document, so the
  correlated form would have to render as a top-level `$expr` over `$filter`/`$anyElementTrue`.
- **`Contains` over an owned collection** (`b.Posts.Contains(post)`) — whole-element equality again.
- **Relativizing `TryResolveOwnedFieldPath`** to supersede the blanket `IsDocumentRoot` decline (§4) — a
  clean, self-contained follow-on, explicitly not bundled here.

## 8. Slice 0 — throwaway de-risking spike

A throwaway branch, reverted after a written findings doc (per the project's spike-first practice; the last
two slices each had a plan-invalidating surprise caught this way). It must settle:

1. **What expression tree EF actually hands us** — the highest-risk unknown, and a direct repeat of the
   previous slice's plan-invalidating surprise (EF rewrote `p.Home.City` into `EF.Property` calls). Dump the
   actual tree for `Any(pred)`, `Any()`, `!Any(pred)`, nested `Any`, and the reached-through-owned-ref form.
   **The matcher must be written against what EF emits, not what the C# source looks like.**

   **ANSWERED (spike, GO).** Every shape arrives as:

   ```
   Queryable.Any(Call(AsQueryable, [<EF.Property/Member hop chain>]), Quote(lambda))   // Any(pred)
   Queryable.Any(Call(AsQueryable, [<EF.Property/Member hop chain>]))                  // Any()  — 1-arg
   ```

   Always `Queryable.Any` (verified by `Method.DeclaringType`), never `Enumerable.Any`; the lambda is always
   `Quote`-wrapped. **None** of the three hypothesised shapes occurs — no
   `MaterializeCollectionNavigationExpression`, no `Queryable.Any(Queryable.Where(...))` pair. This is
   *simpler* than assumed: strip exactly one `AsQueryable()` layer from `Arguments[0]` and hand the rest to
   the existing `TryGetMemberOrEFProperty` hop walker unchanged. Nested `Any` and the through-owned-ref form
   compose for free, because an inner `Any`'s source has the identical shape rooted on the element parameter.
   Negation adds no new shape — it is the pre-existing `UnaryExpression{Not}` wrapping the same `Any` call.
2. **`$elemMatch: {}` validity** — does an empty `$elemMatch` document match every non-empty array (a
   uniform alternative for `Any()`), or is it a server error? Either way the `.0`/`$exists` form is the
   choice, but the answer belongs in the findings.
3. **Does `$expr` work inside `$elemMatch`?** Determines whether the §5 classifier is strictly necessary or
   merely index-motivated. The classifier ships regardless; the finding calibrates the comment.
4. **Empty / missing / null-array parity** — LINQ `Any` vs. `$elemMatch` and `.0`/`$exists` for: populated,
   empty array, missing field, explicit BSON `null`.
5. **Converter / `BsonRepresentation` element leaf** — confirm empirically that
   `Any(p => p.<convertedProp> == v)` matches driver-LINQ with no guard (the D1 property inherited from the
   previous slice).
6. **Is the `MongoFieldPrefixRewriter` path reachable?** Construct an owned `SelectMany` whose inner filter
   contains an `Any`, and confirm whether it reaches the rewriter (and therefore whether the new case is
   load-bearing today or defensive).
7. **Blast radius** — which `OwnedEntityTests` cases actually flip to native, and whether the `NativeOnly`
   spec delta is genuinely zero.

**Gate:** if the EF tree shape makes path resolution unsafe, or if multi-condition semantics cannot be
guaranteed, narrow the slice (e.g. single-condition only) and re-scope before touching production code.

## 9. Testing & verification

- **New `NativeOwnedCollectionPredicateTests`** (functional): for each shape — populated / empty / missing
  array, single- and multi-condition, negated, nested `Any`, reached-through-owned-ref — assert
  **`Native` == `DriverLinq`** values *and* that the shape **succeeds under `NativeOnly`** (routing proof),
  plus an MQL assertion pinning the emitted `$elemMatch` (multi-condition especially, to lock the
  one-element-satisfies-all semantics).
- **Decline tests**: field-to-field / arithmetic element predicate, whole-element equality, primitive-element
  collection, `All`, `.Count`, two-scope quantifier, reference (non-owned) collection — each asserted to
  **throw under `NativeOnly`** (clean decline, not a crash) and to return correct results under `Native`.
- **Unit tests**: `TryResolveOwnedCollectionPath` (relative path building, owned-ref intermediate hops,
  decline on reference/primitive/non-final-collection hops, `EF.Property` hop spelling, two-scope decline);
  the renderer's document shapes (`$elemMatch`, negated, `Any()`, nested); the
  `IsQueryDialectRenderable`/`RenderNode` pairing; and the `MongoFieldPrefixRewriter` case (array path
  prefixed, element predicate untouched).
- **Flips handled**: `OwnedEntityTests` cases verified still green and confirmed as the oracle; `NativeOnly`
  EF10 spec sweep re-run and the delta confirmed zero (or explained).
- **Full `/test-all` EF8 / EF9 / EF10 green** (foreground, per-version isolated testcontainers).
- All new types `internal`; `#if`-clean across EF8 / EF9 / EF10; nullable-annotated; not a break
  (fallback→native, results unchanged).
- `Query/AGENTS.md` as-built note: the new node, the relative-path decision and why it differs from the
  landed absolute-path resolver, the query-dialect classifier and its sync requirement, and the deferral list.

## 10. Open questions — all resolved by the spike (verdict GO)

- **EF expression-tree shape (§8.1):** `Queryable.Any(Call(AsQueryable, [hop chain]), Quote(lambda))`, plus a
  1-arg form. Simpler than any hypothesis; reuses the existing hop walker. ✅
- **`$expr` inside `$elemMatch` (§8.3):** a hard server error. The classifier is a correctness gate. ✅
- **`MongoFieldPrefixRewriter` case (§8.6):** load-bearing today; must land atomically with the translator
  case, rewriter first. ✅
- **`$elemMatch: {}` (§8.2):** legal, and equivalent in effect to the chosen `"path.0": {$exists: true}`
  form. The `.0`/`$exists` choice stands. ✅
- **Empty / missing / explicit-null array parity (§8.4):** all four states agree with the LINQ oracle for
  both the `$elemMatch` and `.0`/`$exists` forms, negated and not. Separately confirmed live that the
  rejected dotted-path form matches a document whose two conjuncts hold on *different* elements (1 match vs.
  `$elemMatch`'s correct 0) — the silent-wrong-data bug approach B would have shipped. ✅
- **Converted element leaf (§8.5):** a `HasConversion<string>()` element scalar matches only when the
  constant is serialised through the property's serializer (`{Rank:'2'}` matches, `{Rank:2}` returns empty).
  The existing `TranslateValue`/`MongoValueRenderer` path already does this, so no new guard is needed —
  D1 re-confirmed empirically for an element-scoped leaf. ✅
- **Which `OwnedEntityTests` cases flip (§6):** the eight member-access cases flip to native; `:160`/`:173`
  (whole-element equality) do **not**, as predicted — though via EF's `Contains` rewriting rather than a
  same-shape decline. All are result-only assertions, so none need changing. ✅
