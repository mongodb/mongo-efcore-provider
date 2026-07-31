# Native LINQ Translation (EF-322) — Status Report

*Generated 2026-07-26 · last updated 2026-07-30 · `NativeQueryOngoing` tip `1b4c1d6` (stacked on `main`,
unmerged), plus owned-data slice 7 on the still-unsquashed branch `EF-322-owned-collection-count-projection`,
plus owned-data slice 8 squashed onto the unmerged branch `EF-360` (its SHA is deliberately not cited here — a
commit cannot cite itself; record it when the branch lands on `NativeQueryOngoing`).*
*Header-hash correction: this line previously read "branch tip `b087957`" and §7 previously cited `7532b15`.
Verified with `git log`: `b087957` is owned-data slice 5 (`All` predicates), two slices behind; `7532b15` is
not in the shipped history at all — it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`, and the squashed slice-6 commit is `1b4c1d6`. Both are
corrected to `1b4c1d6`.*
*Test measurements below are point-in-time against the **EF10 specification suite**. The §7 two-sweep totals
were **re-measured on slice 7** (`ab886fa`); the §7.1/§7.2 breakdowns are still the `1dd7862` measurement,
adjusted only for the two tests that have since flipped (see the note in §7) — the §7.1 per-class breakdown was
re-derived from the slice-7 `nativeonly.trx` and matched it row for row.*

---

## 1. The epic in one line

Epic **EF-322 — "Native LINQ query provider (ground-up rebuild)"** replaces the *translation* half of
the Query subsystem: the provider builds MongoDB aggregation pipelines (MQL) itself from a canonical query
AST and uses the C# driver only to *execute* them (BSON, cursors, sessions, transactions). Driver-LINQ
remains as a gated fallback (`MongoQueryMode.DriverLinq`) until native reaches parity, then the delegation
code is deleted.

**Native is already the default execution path.** Per the provider's versioning rubric this is *not* a
breaking change: query results are unchanged, any shape native does not support falls back automatically,
and `UseQueryMode(MongoQueryMode.DriverLinq)` restores the previous path.

Query modes:

- `Native` (default) — build the pipeline natively; silently fall back to driver-LINQ for unsupported shapes.
- `DriverLinq` — always use the driver's LINQ provider (the pre-EF-322 path).
- `NativeOnly` — native or bust; throw `NativeTranslationNotSupportedException` instead of falling back
  (a diagnostic mode — a full run is a "what actually goes native" report).

---

## 2. Sub-project scoreboard (7 planned)

| SP | Scope | Ticket | Status |
|---|---|---|---|
| SP1 | AST foundation — filter / sort / paging | EF-323 | ✅ Done |
| SP2 | Predicate breadth — `$expr` renderer + operator long tail | EF-329 | ✅ Done |
| SP3 | Projection pushdown — server-side `$project` | EF-331 | ✅ Done |
| SP4 | Scalar cardinality — Count / First / Any / aggregates | EF-336 | ✅ Done |
| SP5 | Collection Includes | EF-339 | 🟡 Flat collection Include done; several shapes deferred |
| SP6 | Remaining operators — GroupBy, SelectMany, set-ops, Distinct, OfType, non-canonical paging | EF-344 / EF-347 | 🟡 Largely done; VectorSearch + long tail deferred |
| SP7 | Materializer perf — one-pass stream → POCO | — | 🟡 **Phase 1 done** (one-pass materializer, `e38587f`); Phase 2 (streaming breadth) not started |

Beyond the seven planned sub-projects, an **owned-data (embedded-document) work stream** has since landed as
nine further stacked slices — these were not a planned SP, but they are where native coverage grew most after
SP7 Phase 1. (This paragraph read "eight" until slice 9 landed.)

| Slice | Scope | Commit |
|---|---|---|
| 1 | Owned single-reference whole-entity queries go native (+ stream) | `690b487` |
| 2 | Owned-collection whole-entity goes native (+ streams) | `275c90e` |
| 3 | Owned single-ref **sub-property** predicates / sorts / projections (dotted paths) | `2a9b56e` |
| 4 | Owned-collection **`Any`** quantifier predicates → `$elemMatch` | `791037b` |
| 5 | Owned-collection **`All`** quantifier predicates → negated `$elemMatch`; **closes EF-335** | `b087957` |
| 6 | Owned-collection **`.Count`** in a predicate — array-index `$exists` (constant tier) / null-safe `$size` inside `$expr` (parameterized/degenerate tier) | `1b4c1d6` |
| 7 | Owned-collection **`.Count` as a PROJECTION leaf** → `{$size: {$ifNull: […]}}` in `$project`; **partially resolved EF-357 at the time** (bare-scalar form no longer fails translation) — EF-357 was later **fully** closed by EF-358, see below | `f163392` + `0cb1b1b` (branch not yet squashed) |
| 8 | Owned-collection **ARRAY leaf as a PROJECTION leaf** → the array projected by alias inside `$project` (`Select(b => new { b.Title, b.Posts })`); carries **EF-360** (re-characterised) and files **EF-362** | branch `EF-360` (not yet squashed) |
| 9 | Owned-collection **FILTERED `.Count(pred)`** → `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`, native both in a predicate (`$expr` tier only) and as a `$project` leaf; **closes EF-359**; files **EF-365** | branch `EF-359` (not yet squashed) |

No JIRA number was filed for slice 7's native-projection half. Two bugs it *measured* were filed: **EF-358**
(the projection-path null-collapse gap, whose closure also fully closed EF-357 — see §4 and §6) and **EF-359**
(filtered `Count(pred)` in a projection hard-fails in every mode). **Both are now CLOSED** — EF-358 by its own
slice, EF-359 by slice 9 above, which in turn filed **EF-365**. See §6.

Refactor interludes (not user-facing): EF-330 (extract `MongoSelectDefinition`), EF-332 (separate the
native-translation layer from QMTEV), EF-334 (centralize the is-native gate into `ClassifyNativeDisposition`).

**Delivery mechanics.** Native sub-projects ship as stacked branches on `NativeQueryOngoing`, one squashed
commit each: SP1 → SP2 → SP3 → SP4 → SP5 → SP6 (GroupBy / set-ops / Distinct / OfType) → EF-347 SelectMany
slices → `1dd7862` → SP7 Phase 1 (`e38587f`) → owned-data slices 1–6 above → tip `1b4c1d6`, with slice 7 on an
unsquashed branch on top. Nothing is merged to `main` yet — the whole native stack lands at parity/cutover.

---

## 3. What's native today

- **Filtering / sorting / paging (SP1–SP2).** Single-collection whole-entity queries generate the
  `BsonDocument[]` pipeline directly. Predicate breadth: nullable equality / `== null` (IS NULL),
  collection `Contains` → `$in`/`$nin`, `string.StartsWith`/`EndsWith`/`Contains` → `$regularExpression`,
  and field-to-field / arithmetic comparisons → `$expr`. Predicate rendering prefers an index-usable query
  dialect; `$expr` is the last resort.
- **Projection (SP3 + EF-347 arithmetic slice).** Terminal anonymous-type / DTO projections over top-level
  member accesses → native `$project`. **Numeric-arithmetic computed leaves** (`+ - * / %`, e.g.
  `new { Total = o.Price * o.Qty }`) go native via `MongoExpressionTranslator.TryTranslateValue`, in a plain
  terminal `Select`, after a whole-entity set-op, and as a set-op operand.
- **Scalar cardinality (SP4).** Entity reducers (`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`) via
  synthesized `$limit`; scalar aggregates (`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) via
  `$count`/`$group` with an explicit empty-input contract (`MongoEmptyAggregateBehavior`).
- **Collection Include (SP5).** Single-level collection `Include` + projected collection `.Count` (`$size`)
  via a flat `$lookup` (no `$unwind`), materialized through the DOM shaper.
- **GroupBy (SP6 / EF-344).** `GroupBy(key).Select(aggregate)` → `$group` + flattening `$project`.
  Scalar/composite/DTO keys; `Count`/`LongCount`/`Sum`/`Average`/`Min`/`Max` accumulators over plain field-refs.
- **Set operations, Distinct, OfType (SP6 / EF-347).** Whole-entity terminal `Union`/`Concat` and
  `Intersect`/`Except` (source-tagging pipeline); projected `Distinct` (degenerate `$group`);
  `OfType<TDerived>()` over TPH (discriminator `$eq`/`$in` conjunct).
- **SelectMany (SP6 / EF-347 — the just-finished tail):**
  - Owned-collection `SelectMany` projecting element members (inner-`Select`, explicit, query-syntax, bare-element)
  - Owned filtered-inner and owned correlated-beyond-outer
  - Reference (FK-correlated) `SelectMany`: projected + bare-whole-entity, inner-element filter, filter
    correlated beyond the FK
  - Nested (exactly two-level) reference `SelectMany` → two chained `$lookup` + `$unwind`
  - Single-scope arithmetic computed leaf inside a SelectMany trailing projection (the final tail item, tip `1dd7862`)
- **Materialization: one-pass streaming (SP7 Phase 1, `e38587f`).** The native streaming materializer is now
  *one-pass*: a per-execution `MongoEntityMaterializerSerializer<TEntity>` is the `Aggregate<TEntity>` output
  serializer, so deserialize **is** materialize — no intermediate `RawBsonDocument`, no second reader/context.
  Allocation vs pre-SP7 native: whole-entity no-track 19.1→5.4 MB (−72%, ~1.73× the raw-driver floor), `Where`
  9.6→2.8 MB, tracked 25.2→11.5 MB; wall-clock ≈ the driver floor. Materialization-only — **zero** query-shape,
  result or eligibility change.
- **Owned (embedded) whole-entity queries (owned-data slices 1–2).** A whole-entity query over an entity with
  owned single-reference navigations (`OwnsOne`, nested) *or* owned collections (`OwnsMany`, incl. mixed and
  shared-CLR-type) now goes native — previously *always* fell back. Root cause was the **gate**, not
  materialization: EF auto-includes owned navs as `Select(x => IncludeExpression(x, nav))`, which matched no
  pass-through predicate. Flat / mixed / shared / empty owned collections **stream** via SP7 Phase 1; a
  collection whose element carries a further navigation, and collection-of-collection, route to native DOM.
- **Owned single-reference sub-property dotted paths (owned-data slice 3).** A predicate, sort key, or
  projection leaf reaching *through* owned single-reference navs to a scalar leaf — `Where(e => e.Home.City == x)`,
  `OrderBy(e => e.Home.Geo.Country)`, `Select(e => new { e.Home.City })` — resolves to a dotted document path.
  One shared gate (`TryResolveMember`) lights up all three surfaces at once.
- **Owned-collection quantifier predicates — `Any` *and* `All` (owned-data slices 4–5).** `Any()`/`Any(pred)`
  and `All(pred)` over an owned collection navigation, negated forms, nesting in either order, and collections
  reached through owned single-reference hops. `Any(pred)` → `$elemMatch`; bare `Any()` → an array-index
  `{"path.0": {$exists}}` test (correct for empty/missing/null arrays alike, unlike `{$ne: []}`); `All(pred)` →
  `{path: {$not: {$elemMatch: ¬pred}}}`, which is also correct for empty/missing/null arrays because LINQ's
  `All` is vacuously true there. An owned `SelectMany` whose inner filter is itself an owned-sub-collection
  `Any` — previously a hard fail in every mode with no driver-LINQ oracle — also goes native.
  **The `All` half rests on a new exact-complement negator (`MongoExpressionNegator`), which also closed
  EF-335** (top-level `All` with a comparison predicate). Its central rule, verified against a live server:
  `$eq`/`$ne` may be **inverted** because they partition every BSON value, but the four relational operators
  must be **`$not`-wrapped**, because `{$gt: 5}` and `{$lte: 5}` do *not* partition — neither matches a field
  that is missing, null, or of another BSON type, so inverting them would report `All == true` where LINQ says
  false. Index note: root-scope `{f: {$not: {$gt: v}}}` is IXSCAN, but the owned-collection `All` form is a
  COLLSCAN — a deliberate correctness-over-index trade (the index-friendly alternative returns wrong answers),
  and the already-shipped `!Any(...)` form scans equally. See
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for both quantifier notes.
- **Owned-collection `.Count` in a predicate (owned-data slice 6).** `Where(b => b.Posts.Count > 2)` and all
  six comparison operators, both operand orders, `.Count`/`.Count()`/`.LongCount()`, a constant *or*
  parameterized threshold, a count reached through owned single-reference hops, and a count nested inside a
  quantifier's element predicate now go native — previously always fell back. A constant threshold renders as
  the query-dialect array-index existence test (`{"path.k": {$exists: true|false}}`) — the same family bare
  `Any()` already used, now unified with it as `Count >= 1`; a parameterized or degenerate threshold renders
  `$expr` over a null-safe `$size` (`$ifNull` maps a missing/`null` array to `[]`, since bare `$size` against
  either is a hard server error). Negation *inverts* the operator (an exact complement, since `$exists`
  partitions the value space) rather than `$not`-wrapping it, the documented exception to the `All` slice's
  `$not`-wrap rule. Index note, as measured (not assumed): all four relational array-index forms come back
  **COLLSCAN** with both a collection- and leaf-level multikey index present — the form is still required
  regardless, since it is the only one legal inside `$elemMatch` and the only one correct for missing/null
  arrays. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s dedicated `.Count` note for the full
  two-tier mechanism, the negator exception, and the settled finding that EF Core rewrites `Count() > 0` into
  `Any()` upstream (so that one spelling is unreachable via ordinary LINQ for the constant-tier `GreaterThan`
  arm at `n = 0`).
- **Owned-collection `.Count` as a PROJECTION leaf (owned-data slice 7).** An owned-collection count appearing
  as a *leaf* inside a terminal anonymous-type or DTO projection — `Select(b => new { b.Title, N = b.Posts.Count })`,
  the named-DTO spelling, `.LongCount()`, a count reached through owned single-reference hops
  (`b.Home.Notes.Count`), and several count leaves side by side — now goes native as
  `{$size: {$ifNull: ["$path", []]}}` inside `$project`. It reuses the `MongoSizeExpression(nullSafe: true)`
  node and the renderer arm slice 6 added: no new expression node, no new renderer arm. Before this slice the
  shape threw `ArgumentException` in **all three** query modes (measured on unmodified `src/`; the earlier
  documentation had implied only the *bare-scalar* form hard-failed — see §4). The binder gate accepts the leaf
  only when the translated node **is** a `MongoSizeExpression`, not merely when translation succeeded. Keeping
  that gate narrow is still the right call, but the MECHANISM this file gave for it was wrong and is corrected
  here: it used to say a bare constant/parameter leaf "renders as a bare value, which `$project` reads as an
  inclusion flag (`{X: 1}`) rather than a literal" — implying silently wrong data. As MEASURED (gate widened to
  plain `TryTranslateValue` success, then the tests run): `X = 5` and a captured-parameter leaf return
  **correct** values, folded client-side, leaving only a junk `X: 5` in the emitted `$project`; `X = 0` and
  `X = false` instead **abort the command** — `MongoCommandException: Invalid $project :: caused by :: Cannot do
  exclusion on field X in inclusion projection` — because `$project` reads `0`/`false` as an EXCLUSION flag. So
  what the narrow node-kind gate keeps out is a hard abort on a falsy constant, not a silent misread; see
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s count-projection note for the full measurement and the
  test that now pins it. The bare-scalar form
  (`Select(b => b.Posts.Count)`) is deliberately *not* native — a bare-scalar projection body never populates
  `Projection`, which is the SP3-wide bare-scalar boundary, not a count-specific one; see §4 for what it does
  instead. **One divergence worth knowing, measured and pinned:** for this shape `DriverLinq` does *not* return
  equivalent results to `Native` on ragged data. Present arrays agree; a **missing or explicitly-null** array
  makes `DriverLinq` raise `MongoCommandException` ("the argument to `$size` must be an array"), because the
  driver's LINQ provider renders a bare server-side `$size` with no `$ifNull`, while native's `$ifNull` form
  answers 0. Before this slice both modes threw, so nothing regressed — but `UseQueryMode(DriverLinq)` is not an
  equivalent-results escape hatch here, and driver-LINQ is also where a projection mixing a count leaf with a
  binder-declined leaf lands under the default `Native` mode.
- **Owned-collection ARRAY leaf as a PROJECTION leaf (owned-data slice 8, branch `EF-360`).** An owned
  entity-COLLECTION navigation appearing as a *leaf* inside a terminal anonymous-type or DTO projection —
  `Select(b => new { b.Title, b.Posts })` — now emits a server-side `$project`
  (`{ $project: { Title: "$Title", Posts: "$Posts", _id: "$_id" } }`) and reads the array back from the
  projection **alias**, instead of falling back to `aggregate([])` and folding the projection client-side over
  whole documents. **Results are unchanged** — a bandwidth and allocation win, not a correctness fix. Two new
  `internal` types (`ArrayAliasProjectionExpression` and a shared `IArrayProjectionExpression`, the latter also
  implemented by the pre-existing `ObjectArrayProjectionExpression`); **no new array-source code was needed** on
  the read-back side, contrary to what the design predicted. Admissibility turns on two rules, both
  found by MEASUREMENT rather than reasoned ahead — though not by the same kind of bug: rule (1) via a measured
  **silent** wrong-data bug, rule (2) via a measured **throw** whose silent variant is mechanism-derived (not
  executed) and pinned by the colliding-alias test: **alias agreement** (the leaf's alias must equal the
  navigation target's containing element name, and the navigation must be declared on the query root) and
  **sibling readability** (when an array leaf is present every non-array sibling must also be readable off a
  whole, un-projected document). Both exist because the shaper is alias-addressed from *translation* time while
  native-vs-fallback is decided *later*, so a fallback hands that shaper a whole document. Declined and still
  fallback: the bare spelling (§5), an `OwnsOne` hop (**EF-362**), a renamed alias or element, a
  non-whole-document-readable sibling (which, as the final review named explicitly, ALWAYS includes a
  primary-key sibling — a root PK's element name is always `_id` while its alias is the CLR name, so no
  ORDINARY naming choice can satisfy the rule; a `_id = b.Id` alias spelling *would* satisfy it — read, not
  executed — and is an untested admitted case for a future slice), a reference (non-owned) collection, an element type carrying its own
  **eager-loaded** navigation (**EF-360** — narrowed at the final review from "any navigation", which also
  over-declined an ordinary `WithOwner` lazy inverse back-reference), and a projected set-op operand. Zero spec
  delta on both axes; three-version sweep 0 failures. *This entry is a summary only* — the full as-built mechanism, every guard with the bug that
  motivated it, the measured set-op flips, and the coverage gaps live in
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s array-valued-projections note.
- **Owned-collection FILTERED `.Count(pred)` (owned-data slice 9, branch `EF-359`).** A *predicated* count over
  an owned collection is now native in two positions: in a **predicate**
  (`Where(b => b.Posts.Count(p => p.Rank > 0) > 2)`, all six comparison operators, either operand order,
  constant or parameterized threshold, through owned single-reference hops) and as a **projection leaf**
  (`Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })`, plus `LongCount`, the named-DTO
  spelling, sibling leaves, and — as an unplanned incidental widening of the pre-existing EF-347 arithmetic
  branch — an arithmetic wrapper, `new { X = b.Posts.Count(pred) * 2 }`). A **third** unplanned widening, found at
  the whole-branch review, also went native: a filtered count inside an **owned `SelectMany`'s inner filter**
  (`SelectMany(b => b.Posts.Where(p => p.Comments.Count(pred) > 1), (b, p) => new { p.Heading })`), which emits a
  top-level `$match` after the `$unwind` — measured to have hard-failed in all three modes at the branch base, so
  a hard-fail → native fix rather than a routing flip. Both render the same
  `{$size: {$filter: {input: {$ifNull: ["$path", []]}, as: "e", cond: …}}}`, always through the
  `$expr`/aggregation tier: unlike the *unfiltered* `.Count`, a filtered count has **no** query-dialect
  array-index (`$exists`) form, and that absence is enforced structurally — a new sealed sibling node,
  `MongoFilteredSizeExpression`, rather than a flag on `MongoSizeExpression`, so the Tier-1 renderer, the
  query-dialect classifier and the negator all fail **closed** by construction. (A flag would have let Tier 1
  answer the *unfiltered* count's question — wrong rows, silently, under default `Native`.) The predicate
  spelling previously fell back with correct results; the projection spelling previously **crashed** in every
  mode (**EF-359**, now closed). Two things worth carrying forward: `$ifNull` is mandatory rather than
  defensive (without it a missing or explicitly-null array is a hard server error that aborts the aggregate),
  and a relational or `== null` element predicate over a **nullable** element field can return a different
  *number* from in-memory LINQ on ragged data — native and `DriverLinq` agree with each other, both differ from
  LINQ, because one BSON total order (`missing < null < numbers`) distinguishes two values the CLR collapses
  into a single `null`. That divergence is an **accepted, documented owner ruling**, not a defect. See
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s "EF-359 AS BUILT" note for the full as-built account,
  every decline, and the residuals that still hard-fail.

---

## 4. What still falls back to driver-LINQ

**By design (correct results under `Native`/`DriverLinq`; throws only under `NativeOnly`):**

- **Computed long tail** — string transforms (`ToUpper`/`Substring`), date-part extraction, `Math.*`,
  type-changing casts, integer-result `Divide` (Guard A: `$divide` is non-truncating).
- **Reference `Include`**, nested/transitive `ThenInclude`, filtered `Include`, collection-of-collection
  Include (lookup/streaming machinery built but dormant).
- **Non-native GroupBy shapes** — computed keys (`g.OrderDate.Year`), computed accumulator operands, bare
  `GroupBy(key)` terminating on `IGrouping`, user `resultSelector`, post-group slot operators
  (Where/OrderBy/Skip/Take as HAVING), correlated / cross-collection keys.
- Bare-scalar & whole-entity `Distinct`; non-whole-entity / non-terminal / mismatched set-ops.
- Contains / ElementAt / Last; computed aggregate selectors. (**`All` with a comparison predicate — EF-335 —
  is NO LONGER on this list: closed by the owned-data slice 5 negator.**)
- Guarded-out for correctness: value-converter / non-default `BsonRepresentation` operands (arithmetic,
  GroupBy keys, Distinct keys, OfType discriminators).
- **Owned-collection predicate/projection long tail (EF-322), as it stands after the `Any`, `All`,
  `.Count`-in-a-predicate *and* `.Count`-as-a-projection-leaf slices:** an embedded-collection **array**
  projection (`Select(b => b.Posts)`; this entry used to read `Select(b => b.Posts.Count)` — the **count** half
  of it moved out when slice 7 landed, see the next bullet and §3), a
  non-query-dialect owned-collection element predicate (field-to-field / arithmetic — no query-dialect form to
  put inside `$elemMatch`, and for `All` no exact complement either), a **correlated** element predicate (one
  referencing the enclosing entity — declined by a dedicated guard, because `$elemMatch` cannot reference the
  enclosing document at all), and a two-scope (cross-scope, inside a `SelectMany`) owned quantifier. An
  owned-COLLECTION intermediate hop in a dotted sub-property path also still declines (slice 3 covers
  single-reference hops only).
- **Bare-scalar owned-collection count** (`Select(b => b.Posts.Count)`). This entry previously read
  "hard-fails in every mode"; slice 7 changed that and the wording is corrected here. It no longer fails
  translation. It is still not native (bare-scalar projection bodies never populate `Projection`), so it takes
  the fallback path — and there, as measured, the count is folded **client-side**: the emitted pipeline is
  `aggregate([])`, no `$project` and no `$size`, so the whole document including the entire array is fetched
  and counted in process. Results are **correct for every array state** — a missing or explicitly-null stored
  array used to throw `ArgumentNullException` at materialization; the EF-358 fix (2026-07-29, see the standalone
  fact below) closed that residual, so it now returns `0` like every other path. `NativeOnly` declines cleanly.

**Hard-fails in every mode (no driver-LINQ oracle):** cross-collection SelectMany forms outside the native
slice, three-level+ nested SelectMany, whole-outer SelectMany, and any operator composed *after* a native
SelectMany (shaper-rebuild limitation). Also — measured by slice 7, pre-existing — an **interposed operator**
(`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat`) between an owned-collection `Select` and a terminal
operator (duplicate-key `ArgumentException` from `_collectionShaperMapping.Add`; recorded as a comment on the
EF-322 epic).

**The whole-shape "filtered count in a projection" entry has MOVED OFF this list — corrected in place, not
annotated beside its stale text.** It used to read: a filtered count in a projection
(`Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`) throws `InvalidOperationException` identically under
`Native`, `DriverLinq` and `NativeOnly` (**EF-359**). That was accurate when slice 7 measured it and is no
longer: owned-data slice 9 (branch `EF-359`) made that shape **native**, and closed EF-359 — see §3 and §6. The
precise disposition of what is left of the family, all of it NARROWER than the shape that moved:

- **Native:** the wrapped projection leaf (`new { N = b.Posts.Count(pred) })`, `LongCount`, the named-DTO
  spelling, sibling leaves, owned single-reference hops, an arithmetic wrapper (`new { X = ...Count(pred) * 2 }`),
  and the predicate spelling (`Where(b => b.Posts.Count(pred) > 2)`).
- **Falls back gracefully** (correct results under `Native`/`DriverLinq`, throws only under `NativeOnly`): in
  the *predicate* position — a correlated element predicate, a non-renderable element predicate, a
  primitive-element collection, a filtered count nested inside a quantifier, a negated filtered-count
  comparison; and a reference (non-owned) collection filtered count anywhere.
- **Still hard-fails in every mode** (`InvalidOperationException` at translation time, so `NativeOnly` gets the
  identical exception rather than a clean decline): in the *projection* position — a non-renderable element
  predicate (**EF-365**, where removing the `CanRender` guard would turn this into a working fallback — measured),
  a correlated element predicate (wrapped *and* bare), a primitive-element collection, the
  `Posts.Where(pred).Count()` spelling, a bare spelling whose predicate closes over a captured local, and
  arithmetic over a *bare* count (`Select(b => b.Posts.Count(pred) * 2)` — the count call is not the root; the
  *wrapped* arithmetic form above is native).
- **Not native, correct values:** the bare spelling `Select(b => b.Posts.Count(pred))` — the SP3-wide
  bare-projection boundary, not a count-specific one — folded client-side over `aggregate([])`.

**CLOSED (EF-358, 2026-07-29) — and the root cause is corrected here, not just the status.** This paragraph
used to describe the gap as a whole-entity-vs-projection split: whole-entity materialization normalizes a
missing/explicitly-null embedded array to an empty list, the projection path does not. **That framing was
measured false.** Pre-fix, *nothing* normalized on *any* path — `MongoProjectionBindingRemovingExpressionVisitor.
IncludeCollection` skips its fixup loop entirely when `relatedEntities` is `null`, so a materialized navigation
kept whatever the CLR class's own field initializer left behind (`null` for a plain `{ get; set; }`, `[]` for one
written `= []`). The earlier "whole-entity normalizes" reading was that initializer masking, not provider
behavior — the probe model that produced it happened to declare `Blog.Posts = []`. **Post-fix, behavior is
uniform and initializer-independent on every path** (whole-entity, `Include`, bare and wrapped projection),
every query mode, every cardinality: a missing or explicitly-null stored array now materializes as an empty
collection everywhere. Mechanism: the null-collapse conditional was deleted from
`BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case, and a `Coalesce` to an empty
`BsonArray` was added at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s
`CollectionShaperExpression` case, feeding `PopulateCollection` through the navigation's own
`IClrCollectionAccessor` (so a non-`List` navigation is correct for free). The cross-visitor contract — the
injector's assignment must keep a `UnaryExpression` right-hand side, because `VisitBinary` hard-casts it — is
why the coalesce sits at the point of use rather than folded into the injector's own assignment; folding it in
throws `InvalidCastException` for every collection shaper in every mode. Consequences: the bare-scalar count
fallback now returns `0` instead of throwing `ArgumentNullException`, and **EF-357 is now FULLY closed** (see
§6). Primitive-collection *properties* are unaffected — that is a property-serializer path, not a
`CollectionShaperExpression`. See the rewritten note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for
the full mechanism, the parity-claim split (bare vs. wrapped count), and the `TypeAs` conflation.

**Not native at all:** `VectorSearch`; non-TPH `OfType`.

---

## 5. Deferred items still on the epic

- **SP7 Phase 2 — streaming breadth (Phase 1 landed; Phase 2 not started).** Phase 1 delivered the one-pass
  materializer (see §3). Still deferred: **reducer/aggregate** streaming, **collection-Include array**
  streaming, and **reference-Include** streaming — the last blocked behind making reference `Include` native at
  all. Those shapes still route through the DOM shaper, not the streaming one. Also minor: delete the
  now-dead `RawBsonDocument` branch + `BsonRowReader`, which Phase 1 made unreachable.
- **Parity cutover.** Once native reaches parity: retire the driver-LINQ fallback and delete the delegation code.
- **Minor SelectMany follow-ons (EF-347 leftovers):** cross-scope computed leaf (`o.Discount * i.Price`),
  the inner-`Select`-form computed-leaf binder.
- **Owned-collection follow-ons (EF-322), as they stand after slices 4–9 — in the order they are actually
  nearest.** "Embedded-collection projections" is no longer the nearest one; slice 7 took the count leaf
  natively, slice 8 took the wrapped ARRAY leaf, and **slice 9 took the filtered `Count(pred)`, which this list
  used to rank FIRST — that bullet is struck and the rest re-ranked in place** (it read: "Filtered `Count(pred)`
  in a projection (EF-359) … a bug fix of the same shape as EF-357 … the *graceful-fallback* assumption was
  written before the measurement and does not [stand]". Both the crash characterization and the `$size`-over-
  `$filter` rendering prediction held up; the shape is now native and EF-359 is closed — see §3, §4 and §6).
  What is nearest now:
  1. **Array projections — the WRAPPED spelling is DONE** (owned-data slice 8, branch `EF-360`, 2026-07-30).
     `Select(b => new { b.Title, b.Posts })` and the DTO equivalent now emit a server-side `$project` and read
     the array back from the projection alias. **This bullet used to say array projections were "blocked on the
     DOM-shaper mechanism alone" — that was correct for the wrapped spelling (now done) and was NEVER correct
     for the bare spelling**, which carries a second, independent block; the two are separated below. It also
     named the `ObjectArrayProjectionExpression` hard cast as the blocker, which was wrong on its own terms: the
     cast did need widening, but the mixed/fallback path never reached that arm (it takes the inline
     `ObjectArrayProjectionExpression` arm), so nothing was failing there. See
     `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the as-built mechanism, the two admissibility rules
     (alias agreement and sibling readability, each found via a live silent-wrong-data bug), and the declines.
  2. **Bare array projection** (`Select(b => b.Posts)`) — still fallback, and **not** for an array-specific
     reason: a bare (non-`new {...}`) selector body never populates `Projection` at all, which is the SP3-wide
     bare-projection boundary, the same one that keeps `Select(b => b.Posts.Count)` on the fallback path. It
     returns correct results there (`aggregate([])`, projection folded client-side). Lifting the boundary is one
     piece of work covering bare scalars, bare entities and bare arrays alike — see the next bullet.
  3. **Bare-scalar projection pushdown** — the SP3-wide boundary just described; not count- or array-specific,
     and lifting it would light up more than counts.
  4. **`OwnsOne`-hop array leaf (EF-362).** `Select(b => new { b.Title, b.Home.Notes })` is a clean decline,
     pinned by a tripwire test. It needs a *second* mechanism, not a relaxation: for a hop the `$project` alias
     is necessarily FLAT (`"Notes"`) while the document path is NESTED (`"Home.Notes"`), so slice 8's
     alias-agreement invariant ("the alias read and the document-path read resolve to the same place") cannot
     hold — a path-preserving `$project` emitting `{"Home.Notes": "$Home.Notes"}` (which MongoDB renders as
     nested output) plus keeping the document-path read, rather than switching to alias-addressed, is what it
     would take.

  5. **A non-renderable element predicate in a filtered `Count(pred)` projection (EF-365).** Newly filed by
     slice 9. `Select(b => new { N = b.Posts.Count(p => p.Heading.StartsWith("h")) })` hard-fails in every mode,
     and — measured — it does so *because* of a guard (`MongoAggregationExpressionRenderer.CanRender`) whose
     removal makes `Native`/`DriverLinq` return correct values and `NativeOnly` decline cleanly. The guard has no
     correctness role; it was retained on scope grounds. See §6.

  A **correlated** element predicate needs more than a two-scope translator **for the QUANTIFIERS**: `$elemMatch`
  cannot reference the enclosing document, so it would have to render as a top-level `$expr` over
  `$filter`/`$allElementsTrue`. **For a filtered `Count(pred)` the situation is different and easier — recorded
  here because this paragraph previously implied one blanket limit:** a `$filter` `cond` *can* reference the
  enclosing document (`{$gt: ["$$e.Rank", "$Threshold"]}` is legal), so slice 9's correlated decline is a
  deferrable *capability* needing only a two-scope element translator, not an architectural impossibility.
  Relativizing
  the owned single-reference dotted-path scalar resolver (`TryResolveOwnedFieldPath`) the way the quantifier
  resolver is scoped would let a two-scope owned dotted access work without its current blanket decline.

---

## 6. Carried tickets (EF-353…357 filed during EF-347; EF-358/359 during owned-data slice 7; EF-360/362 carried by owned-data slice 8; EF-365 filed by owned-data slice 9; EF-357/EF-358/EF-359 now closed)

| Ticket | Type | Summary | Severity |
|---|---|---|---|
| **EF-353** | Task | Native bare owned-element SelectMany can't materialize **nested owned members** — currently a clean decline (`GetNavigations().Any()` guard); lifting it needs a re-rooted projection mapping | Feature gap, clean decline |
| **EF-354** | Bug | `SelectMany(o => o.Items, (o,i) => o)` (whole-outer, explicit method-call spelling) **crashes** ("Id missing") instead of declining cleanly; query-syntax spelling already declines | Loud crash, not wrong data |
| **EF-355** | Bug | Filtered reference SelectMany: folded-predicate split in `TrySplitCorrelation` can **silently drop a `!= null` inner filter** → returns all children | Silent wrong data, **latent/unreachable** today (EF emits nested, not folded, shape) |
| **EF-356** | Bug | Mixed whole-entity + computed-arithmetic projection (`new { c, Total = c.Age * c.Score }`) returns **silently wrong** values (`Score²`) — mixed shaper has no `BinaryExpression` handling | Silent wrong data, **pre-existing**, pinned by a documenting test |
| **EF-357** | Bug | **CLOSED** (branch `EF-358`, 2026-07-29). Bare embedded-collection `.Count` projection (`Select(b => b.Posts.Count)`) threw `ArgumentException` in **every** query mode — a `MongoProjectionBindingExpressionVisitor` gap, not a native decline. Owned-data slice 7 (`0cb1b1b`) fixed the translation-time `ArgumentException` and made present arrays return correct counts, leaving a missing/explicitly-null-array `ArgumentNullException` at materialization as a residual (that residual was EF-358). The EF-358 fix closed that residual, so `Select(b => b.Posts.Count)` now returns correct counts for every array state | Was: hard fail every mode. Now: correct for every array state |
| **EF-358** | Bug | **CLOSED** (branch `EF-358`, 2026-07-29). Root cause corrected during investigation — it is **not** a whole-entity-vs-projection split; pre-fix, nothing normalized a missing/explicitly-null embedded array on *any* path, and the apparent whole-entity normalization was CLR field-initializer masking (`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection` skips its fixup loop when `relatedEntities` is `null`). Fix: delete the null-collapse conditional from `BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case; add a `Coalesce` to an empty `BsonArray` at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case. Result is uniform, initializer-independent normalization on every path/mode/cardinality; closes EF-357's residual. See §4 and `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the full mechanism | Was: runtime throw / inconsistent shape. Now: closed |
| **EF-359** | Bug | **CLOSED** (branch `EF-359`, owned-data slice 9, 2026-07-30). Filtered `Count(pred)` in a projection (`Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`) threw `InvalidOperationException` in **all three** query modes — a translation-time crash in `MongoProjectionBindingExpressionVisitor.Translate`, before `MongoQueryMode` is read; same shape of defect as EF-357, **not** the graceful decline earlier docs assumed. Mechanism of the fix, in one line: recognize the predicated `Count`/`LongCount` overloads by canonical `MethodInfo` at both the translator and the projection-binding sites, represent the result as a new sealed sibling node `MongoFilteredSizeExpression` (never a flag on `MongoSizeExpression`, so the Tier-1 array-index renderer, the query-dialect classifier and the negator all fail closed), and render `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`. The predicate spelling went native too; the bare spelling now folds client-side with correct values instead of crashing. Narrower residuals remain (§4), one of them filed as EF-365 | Was: hard fail every mode. Now: native, closed |
| **EF-365** | Bug | **Newly filed by owned-data slice 9.** A **non-renderable element predicate** in a filtered `Count(pred)` *projection* — e.g. `Select(b => new { N = b.Posts.Count(p => p.Heading.StartsWith("h")) })` — hard-fails with `InvalidOperationException` in all three modes, where a graceful fallback is demonstrably available. MEASURED: with `MongoAggregationExpressionRenderer.CanRender` gating the branch (as shipped) the query crashes in every mode; with the check removed, `Native` and `DriverLinq` return the **correct** value and `NativeOnly` declines cleanly. So the guard *preserves* a pre-existing crash — it has **no correctness role** (the `$expr`-inside-`$elemMatch` hazard is `IsQueryDialectRenderable`'s job) and the design doc's justification for it is measured false. It was retained in EF-359 on scope grounds only. Fix = delete the call site, then the now-callerless classifier and its unit tests, and re-baseline the pinned residual-decline test. **Breadth still to verify before that ships:** only `StartsWith` was measured — `Contains`/`$in`, unary `Not`, a bare nullable bool, and a MIXED projection (a declining leaf beside an admitted one, where the driver may not emit the alias the shaper reads) are UNVERIFIED | Hard fail every mode, **pre-existing**, pinned by a documenting test |
| **EF-360** | Bug | **STILL OPEN, and RE-CHARACTERISED here — it is *not* "an anonymous projection with an entity-collection leaf throws".** That framing was disproved by owned-data slice 8, which made exactly that shape native. The actual defect: an anonymous **or** DTO projection whose collection leaf's **ELEMENT TYPE has a navigation of its own** throws `ArgumentException` ("does not match the corresponding member type") in **every** query mode, in `MongoProjectionBindingExpressionVisitor.VisitNew`, via the `Queryable.Select`-rebuild → `MatchTypes` short-circuit (`MatchTypes` returns the `List<T>`-typed shaper untouched for an `IQueryable<T>` target, so BCL validation throws at the `newExpression.Update(newArguments)` call). **Cited by METHOD, not by line:** earlier docs quoted `MongoProjectionBindingExpressionVisitor.cs:661`, which this slice's own additions to that file made stale — `:661` is now `return null!;`. It reproduces for a nested owned **collection** and a nested owned **single reference** alike, and the **bare** spelling of the same query on the same model works. It fires at shaper-BUILD time, before `MongoQueryMode` is read, so the mode is irrelevant. Slice 8 declines the shape explicitly (`IsNativeArrayProjectionLeaf`'s element-navigation conjunct) and keeps the failure **byte-identical**, verified by an A-B probe; that conjunct is currently defence-in-depth over a pre-existing structural decline in `TryResolveOwnedCollectionPath` (positive-control-verified) and is documented as such in `Query/AGENTS.md` so it is not deleted as dead code. Same fall-through root cause as EF-357/EF-359 | Hard fail every mode, **pre-existing**, pinned by documenting tests |
| **EF-362** | Task | **Newly filed by owned-data slice 8.** `OwnsOne`-hop array leaf: `Select(b => new { b.Title, b.Home.Notes })` stays a clean decline (falls back, correct results; throws only under `NativeOnly`), pinned by a mutation-verified tripwire test. It is **not** a relaxation of slice 8's rules — for a hop the `$project` alias is necessarily FLAT (`"Notes"`) while the document path is NESTED (`"Home.Notes"`), so the alias-agreement invariant *cannot* hold and lifting the conjunct alone would return a silently EMPTY collection on any fallback path. Needs a path-preserving `$project` (`{"Home.Notes": "$Home.Notes"}`, which MongoDB renders as nested output) plus retaining the document-path read instead of switching to alias-addressed | Feature gap, clean decline |

Of these, **EF-356** (reachable today under `Native`) and **EF-355** (latent) are the two that produce (or
could produce) *silent* wrong data. Confirmed unaffected by the EF-358 fix: neither EF-356 nor EF-355 touches
a `CollectionShaperExpression`'s null/missing-array handling — EF-356 is a mixed-shaper arithmetic-leaf gap and
EF-355 is a predicate-folding gap in `TrySplitCorrelation`, both orthogonal to the two edits EF-358 made.

Also recorded as a **comment on the EF-322 epic** rather than its own ticket (a family of shapes, sharing the
fall-through root cause that EF-357 and EF-359 also had): an interposed `Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat`
between an owned-collection `Select` and a terminal operator hard-fails at translation in every mode with a
duplicate-key `ArgumentException` from `_collectionShaperMapping.Add`. Pre-existing; neither caused nor fixed by
slice 7.

---

## 7. Which tests require driver-LINQ to pass — empirical measurement

Measured by the two-sweep subtraction on the **EF10 specification suite**, most recently re-measured on
owned-data slice 7 (`ab886fa`, the count-PROJECTION slice) with **zero further delta** — see the note below the
table. (The previous revision of this section cited `7532b15` for the `.Count`-in-a-predicate slice. That hash
is not in the shipped history: it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`. The squashed slice-6 commit is `1b4c1d6`, and the figures
below reproduced exactly at both `1b4c1d6` and slice 7.)

> `{ tests requiring driver-LINQ } = { pass under Native } − { pass under NativeOnly }`

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4589** | 0 | 19 | 4608 |
| `NativeOnly` (fallback removed) | **2194** | **2395** | 19 | 4608 |

Because `Native` fails **zero** tests, every `NativeOnly` failure was a `Native` pass — so the set is exact:

> **2395 spec tests currently require the driver-LINQ fallback to pass.** They go green under `Native`
> only by silently falling back; remove the fallback and they throw `NativeTranslationNotSupportedException`.

**Delta since the `1dd7862` measurement: 2 tests** (`NativeOnly` 2192 → 2194 passing, 2397 → 2395 failing).
Both are `NorthwindAggregateOperatorsQueryMongoTest.Select_All` (`async: True`/`False`), which went native via
the EF-335 negator in owned-data slice 5. Its `Native`-mode MQL baseline was also re-baselined in that slice:
the driver fallback's trailing `{ "$project" : { "_id" : 0, "_v" : null } }` stage disappears, which is the
signature of native routing. Results were unchanged.

Everything else measured identical across the two tips — notably, the four owned-data slices *before* slice 5
produced **zero** spec delta, because Northwind has no owned collections or owned sub-property coverage. The
`.Count`-in-a-predicate slice (owned-data slice 6, `1b4c1d6`) measured the **same story a third time**, and the
count-PROJECTION slice (owned-data slice 7, `ab886fa`) a **fourth**: `Native` 4589/0/19 and `NativeOnly`
2194/2395/19 at both, exact match to the `b087957` figures above. Expected, since Northwind has no owned
collections for the count machinery to touch. **Both axes were checked per test on the slice-7 sweep, not just
the aggregate:** `Native` produced an empty failure list (so no `Native`-mode MQL baseline moved — a baseline
that had changed would show up as a failure against its checked-in string), the two runs covered an identical
4608-test name set, and the per-class `NativeOnly` failure breakdown was re-derived from `nativeonly.trx` and
matched every one of the 24 rows in §7.1 exactly, `NorthwindSelectQueryMongoTest` (198) and
`NorthwindIncludeQueryMongoTest` (132) among them — the two classes whose `Customers.SelectMany(c => c.Orders)`
-style tests exercise the `Queryable` switch slice 7 touched. Two cautions for whoever re-measures next:

- **A total of 4608 is correct.** One intermediate measurement during slice 5 reported 4601 (7 low) and was
  wrong; the figure here reproduced exactly on a fresh base-vs-branch run and again on the final three-version
  sweep. Do not "correct" this table downward without a clean re-measurement.
- **Check both axes per test.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline that
  a slice changes — `Select_All` is exactly that. An inventory built only from the `NativeOnly` pass set will
  miss such flips (it missed this one).

Scope note: spec suite, EF10, at this tip. The functional `Native*` tests self-parametrize across modes so
they don't count here; unit tests don't touch a database.

### 7.1 By test class (24 classes)

*Counts originated as the `1dd7862` measurement with one change applied: `NorthwindAggregateOperatorsQueryMongoTest`
236 → 234 for the two `Select_All` tests that went native. That per-class attribution was not re-derived at
`b087957` — only the totals were. It **has** now been re-derived, from the owned-data slice 7 `nativeonly.trx`
(`ab886fa`), and every one of the 24 rows below matched the figure already in the table, summing to 2395.*

| Count | Class |
|---:|---|
| 549 | NorthwindMiscellaneousQueryMongoTest |
| 234 | NorthwindAggregateOperatorsQueryMongoTest |
| 226 | NorthwindWhereQueryMongoTest |
| 208 | NorthwindGroupByQueryMongoTest |
| 198 | NorthwindSelectQueryMongoTest |
| 132 | NorthwindEFPropertyIncludeQueryMongoTest |
| 132 | NorthwindIncludeQueryMongoTest |
| 130 | NorthwindStringIncludeQueryMongoTest |
| 114 | NorthwindSetOperationsQueryMongoTest |
| 104 | NorthwindIncludeNoTrackingQueryMongoTest |
| 78 | NorthwindJoinQueryMongoTest |
| 62 | NorthwindNavigationsQueryMongoTest |
| 56 | VectorSearchMongoTest |
| 56 | VectorSearchExactMongoTest |
| 29 | NorthwindQueryFiltersQueryMongoTest |
| 24 | NorthwindBulkUpdatesMongoTest |
| 14 | NorthwindKeylessEntitiesQueryMongoTest |
| 14 | BuiltInDataTypesMongoTest |
| 12 | NorthwindAsNoTrackingQueryMongoTest |
| 10 | NorthwindDbFunctionsQueryMongoTest |
| 6 | NorthwindChangeTrackingQueryMongoTest |
| 3 | NorthwindCompiledQueryMongoTest |
| 3 | NorthwindAsTrackingQueryMongoTest |
| 1 | NorthwindQueryTaggingQueryMongoTest |

### 7.2 By *why* they fall back (failure-message buckets)

*As measured at `1dd7862`. Not re-derived at `b087957`; the 2-test delta above came out of this set, but which
bucket it left was not re-attributed. Note the "Predicate renderer gap (`Not` over unsupported subtree)" bucket
did **not** empty: owned-data slice 5 taught `RenderUnary` to render `Not` over a query-native comparison, but
a `Not` whose operand is a conjunction or a nested `Not` still declines by design.*

| Count | Cause |
|---:|---|
| 873 | **Non-entity projection not natively representable** — computed / scalar / client-eval projection long tail (`Select` shapes, casts, client methods) |
| 794 | **Query not natively representable** — joins, cross-collection navigation, non-native GroupBy shapes, misc operators |
| 54 | **Reference-nav `$lookup` not supported** — reference `Include` / navigation lookups |
| 13 | Non-constant regex pattern (EF-247) |
| 10 | Predicate renderer gap (`Not` over unsupported subtree) |
| 507 | `Assert.Throws` **exception-type mismatch** — feature unsupported in *every* mode; test pins the *driver-LINQ* exception type, native throws `NativeTranslationNotSupportedException` instead |
| 74 | `Assert.Contains` — expected *error-message text* differs between the two throws |
| 66 | `ArgumentOutOfRangeException` (index) — a materialization / shaper gap surfaced only under `NativeOnly` |

### 7.3 The fallback set splits into two meaningfully different kinds

- **~1744 need driver-LINQ for correct *results*** (the `NativeTranslationNotSupportedException` data
  buckets: 873 + 794 + 54, plus the small regex/renderer ones). These are the genuine coverage gaps —
  remove the fallback and the user gets an exception instead of data.
- **~647 differ only in *failure shape*** (507 exception-type + 74 message + 66 index). These features are
  unsupported in *every* mode (no correct data is produced either way); the tests pass under `Native` only
  because the override pins the *driver-LINQ* exception type/message. Strictly they "require driver-LINQ to
  pass as written," but they aren't lost functionality — at parity cutover these overrides get re-baselined
  to assert the native exception.

Representative examples: `All_client`, `Client_method_in_projection_requiring_materialization_1`,
`Cast_on_top_level_projection_brings_explicit_Cast` (projection long tail); `All_after_GroupBy_aggregate`,
`Anonymous_projection_Distinct_GroupBy_Aggregate` (GroupBy shapes); `VectorSearch_Memory_floats`
(vector search).

### 7.4 How to reproduce

```bash
# Build once (both MONGODB_URI and ATLAS_URI unset → isolated atlas-local container per run)
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"

# Baseline (Native default)
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory <dir>

# Native-only (fallback removed)
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory <dir>

# The tests that require driver-LINQ = the Failed set in nativeonly.trx
# (equivalently: pass-set(native.trx) − pass-set(nativeonly.trx))
```

`MONGODB_EF_NATIVE_ONLY=1` flips every spec context's `DbContextOptionsBuilder` to
`MongoQueryMode.NativeOnly` (`MongoTestStore.AddProviderOptions`), so any query that would otherwise
silently fall back throws instead.

---

## 8. Bottom line

SP1–SP4 are complete; SP5–SP6 are substantially complete with a well-characterized fallback set; the
SelectMany tail (SP6) is finished. **SP7 Phase 1 (the one-pass materializer) has landed**, cutting native
allocation 54–72% to roughly the raw-driver floor. Since then, a **nine-slice owned-data work stream** has
made embedded documents largely native: whole-entity (single-ref and collection), single-ref sub-property dotted
paths, both `Any` and `All` quantifier predicates — the latter closing **EF-335** — `.Count` used in a
predicate, unified with bare `Any()` as one array-cardinality representation, `.Count` as a projection leaf,
an owned entity-**collection** (array) leaf inside a terminal anonymous/DTO projection, and a **filtered**
`.Count(pred)` in both a predicate and a projection leaf — the last closing **EF-359**. (This paragraph said
"six-slice" until slice 8 and "eight-slice" until slice 9; corrected here each time, along with the follow-on
claim below.)

**The remaining native work is SP7 Phase 2 (streaming breadth: reducer/aggregate, collection-Include arrays,
reference-Include) plus the parity cutover that retires driver-LINQ.**

**CORRECTED at owned-data slice 8 — this paragraph previously named the nearest owned-data follow-on as
"array-valued embedded-collection projections (`Select(b => b.Posts)`), blocked on an alias-driven array
read-back mechanism in the DOM shaper". That is now wrong twice over.** First, the alias-driven read-back
mechanism EXISTS: slice 8 made the **wrapped** spelling (`Select(b => new { b.Title, b.Posts })`) native.
Second, the **bare** spelling was never blocked on that mechanism at all — its blocker is the SP3-wide
bare-projection boundary (a bare selector body never populates `Projection`), exactly as §5 bullet 3 now states;
it falls back and returns correct results. **CORRECTED AGAIN at owned-data slice 9:** this paragraph then named
the nearest owned-data follow-on as **EF-359** (filtered `Count(pred)` in a projection), "a translation-time hard
fail in all three modes and therefore a bug fix rather than a fallback→native widening". That characterization
was right, and slice 9 acted on it — EF-359 is **closed**, the shape is native, and it is no longer a follow-on
at all. The nearest owned-data follow-ons are now the ones §5 lists: the **bare** array projection and the
SP3-wide bare-projection boundary behind it, the `OwnsOne`-hop array leaf (**EF-362**), and **EF-365** (a
non-renderable element predicate in a filtered-count projection hard-fails where a graceful fallback is
measurably available). An arithmetic projection leaf containing a count already goes native as an incidental
widening — for a *filtered* count too, also incidentally, and only in the **wrapped** spelling. **This paragraph's parenthetical about the bare-count form was
also STALE; corrected here.** It used to say the bare form "is a separate, pre-existing hard-fail predating this whole work stream" — true
only before owned-data slice 7. Since slice 7 the bare form (`Select(b => b.Posts.Count)`) no longer fails
translation; since EF-358 (2026-07-29) it returns correct results for every array state, including missing or
explicitly-null. See §4 and §6 for the current disposition.

Empirically, 2395 EF10 spec tests still lean on the driver-LINQ fallback — roughly 1744 for correct results
(real coverage gaps) and ~647 only for the expected exception shape (re-baselined at cutover). That number has
barely moved across the owned-data stream, and for a good reason worth remembering: **Northwind has no owned
collections**, so this work stream's coverage gains are proven by the functional `Native*` suites, not by the
spec scoreboard. A flat spec number does not mean a slice achieved nothing.
