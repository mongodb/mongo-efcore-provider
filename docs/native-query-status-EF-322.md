# Native LINQ Translation (EF-322) — Status Report

*Generated 2026-07-26 · last updated 2026-08-05 · `NativeQueryOngoing` tip `9dd6fc15` (= `origin/NativeQueryOngoing`,
working tree clean, stacked on `main`, unmerged).*

> **⚠ READ THIS BEFORE TRUSTING ANY SHA BELOW (added 2026-08-05).** The stack was rebased onto
> `upstream/main` = `58e05a0e` after most of this document was written, so **every SHA cited in §2 and in the
> hash-bookkeeping note below is a pre-rebase hash.** Those objects still exist — the safety branch
> `NativeQueryOngoing-prerebase` keeps them alive — but **none of them is on the current branch**, so
> `git merge-base --is-ancestor <sha> HEAD` fails for all of them. The commit *subjects* are unchanged, so map
> old → new with `git log --oneline upstream/main..HEAD` and match on subject. Four mappings already
> established: slice 7 `cfe873e`→`9b641549`, EF-358 fix `7c199e4`→`46e5a3f8`, slice 8 `33fdc58`→`d63659fc`,
> slice 9 `229294f`→`3483fc60`. The remaining rows have not been re-derived.
>
> **Also stale: §7's measurements.** They were taken at pre-rebase tip `229294f`; six slices have landed
> since (§2's joins table). Treat §7's numbers as a floor of unknown tightness, not as current, and re-measure
> per §7.4 before relying on them.
*Hash bookkeeping, corrected at this revision: the previous header cited tip `1b4c1d6` with slices 7–9 sitting
on unsquashed side branches. **All three have since been squashed onto `NativeQueryOngoing`**, so they now have
real, citable SHAs and the §2 slice table records them: slice 7 = `cfe873e` (was `f163392` + `0cb1b1b`),
slice 8 = `33fdc58` (was "branch `EF-360`"), slice 9 = `229294f` (was "branch `EF-359`"). The EF-358 fix,
which is a bug fix rather than a slice and which the old table did not list at all, is `7c199e4`, and sits
between slices 7 and 8. Earlier corrections retained for the record: `b087957` is slice 5 (`All` predicates),
and `7532b15` was never in the shipped history — it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`.*
*Test measurements below are point-in-time against the **EF10 specification suite**. §7 was **fully
re-measured at this tip (`229294f`) on 2026-07-31** — both sweeps re-run from scratch, and §7.1 *and* §7.2
re-derived from the fresh `nativeonly.trx` rather than carried forward. The totals and the entire per-class
table reproduced the slice-7 figures exactly; §7.2's buckets shifted slightly and are re-stated there.*
*Branch position (2026-08-05): **51 commits ahead of `upstream/main` and 0 behind**. The rebase onto `58e05a0e`
(EF-317, C# driver 3.10.0) that the previous revision listed as outstanding **has been done** — `upstream/main`
is now an ancestor of the tip.*

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
| 7 | Owned-collection **`.Count` as a PROJECTION leaf** → `{$size: {$ifNull: […]}}` in `$project`; **partially resolved EF-357 at the time** (bare-scalar form no longer fails translation) — EF-357 was later **fully** closed by EF-358, see below | `cfe873e` |
| — | *(not a slice — the EF-358 bug fix)* A missing or explicitly-null embedded array materializes as an **empty collection** on every path, mode and cardinality; closes EF-357's residual | `7c199e4` |
| 8 | Owned-collection **ARRAY leaf as a PROJECTION leaf** → the array projected by alias inside `$project` (`Select(b => new { b.Title, b.Posts })`); carries **EF-360** (re-characterised) and files **EF-362** | `33fdc58` |
| 9 | Owned-collection **FILTERED `.Count(pred)`** → `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`, native both in a predicate (`$expr` tier only) and as a `$project` leaf; **closes EF-359**; files **EF-365** | `229294f` |

### The joins / reference-`Include` work stream (§9.8 step 1) — six slices, 2026-08-03…05

This is the *execution order's* step 1 and it is now substantially delivered. All SHAs in this table are
post-rebase and **are** on the current branch.

| Slice | Scope | Commit |
|---|---|---|
| — | **EF-366** — decline a join whose inner sequence is paged (CSHARP-6017). A fix wave found a **second doorway** via `Distinct`; the guard belongs at the shared site, not per entry point | `0162b737` |
| — | **EF-367** — make the four `Include` spec suites fail on wrong data. Filed on a premise ("~40 masked failures") that **measured false — the real answer was zero**; kept because the suites genuinely could not detect wrong data | `5dfb1653` |
| — | **EF-370** — correct required-navigation `$unwind` semantics and stop dropping composed operators (ported) | `7af4190b` |
| 1 | **EF-368** — single-level reference `Include` goes native. The first joins slice | `34a02067` |
| 2 | **EF-372** — scope a transitive join's `$lookup.localField` at any depth, **or decline**. Fixes silent 0 rows at hop 3+ | `6a7a5f3c` |
| 3 | **EF-373** — emit a join's `$lookup` on the correct side of an interleaved operator. Fixes a silently wrong page when `Skip`/`Take` sits *between* two joins | `9dd6fc15` |

Two design facts from these slices that are expensive to re-derive:

- **"Decline" on the transitive-join path is a HARD translation failure, not a graceful fallback.** EF-372
  returns `null` (EF Core's own translation-failure path), which fails in *every* `MongoQueryMode` including
  explicit `DriverLinq` — `UseQueryMode` is not an escape hatch. A graceful `MarkNotNativelyRepresentable()`
  was tried and **measured strictly worse**: the un-rebound inner shaper reaches materialization and throws in
  both modes, because the decline is only ever reached once an EARLIER join's `$lookup` has already been
  registered — at translation time, before `MongoQueryMode` is read — so both paths are already committed to
  the flat shape. (This used to read "because a transitive hop is always a second-or-later join". That is
  FALSE — an owned `SelectMany` produces a transparent identifier too, so a `TransitiveHop` can occur at the
  FIRST join — and it was the premise behind a decline EF-379 shipped and then withdrew as a measured
  regression; see the EF-379 note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`.) This matches how
  reference `SelectMany` and `Intersect`/`Except` already decline.
- **A pure `$sort` relocation has no observable effect on results.** `$sort` and a fan-out `$unwind` commute
  with respect to key order (`$unwind` preserves input order and expands each document into an adjacent
  equal-key run), so only an **MQL stage-order pin** discriminates EF-373's fix — a row-order assertion cannot.
  Measured: reverting to the pre-EF-373 contiguous group turns the stage-order pin red while the ordered data
  assertion still passes.

These slices filed five defects, all in the same neighbourhood and all unreleased — **EF-375, EF-376, EF-377,
EF-378, EF-379**. See §6 and §9.5; the ordering consequences are in §9.8.

No JIRA number was filed for slice 7's native-projection half. Two bugs it *measured* were filed: **EF-358**
(the projection-path null-collapse gap, whose closure also fully closed EF-357 — see §4 and §6) and **EF-359**
(filtered `Count(pred)` in a projection hard-fails in every mode). **Both are now CLOSED** — EF-358 by its own
slice, EF-359 by slice 9 above, which in turn filed **EF-365**. See §6.

Refactor interludes (not user-facing): EF-330 (extract `MongoSelectDefinition`), EF-332 (separate the
native-translation layer from QMTEV), EF-334 (centralize the is-native gate into `ClassifyNativeDisposition`).

**Delivery mechanics.** Native sub-projects ship as stacked branches on `NativeQueryOngoing`, one squashed
commit each: SP1 → SP2 → SP3 → SP4 → SP5 → SP6 (GroupBy / set-ops / Distinct / OfType) → EF-347 SelectMany
slices → `1dd7862` → SP7 Phase 1 (`e38587f`) → owned-data slices 1–6 → `1b4c1d6` → slice 7 (`cfe873e`) →
EF-358 fix (`7c199e4`) → slice 8 (`33fdc58`) → slice 9 (`229294f`, the current tip). **As of 2026-07-31 there
is no unsquashed work in flight** — every slice is on the branch and pushed. Nothing is merged to `main` yet —
the whole native stack lands at parity/cutover.

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
- **Owned-collection ARRAY leaf as a PROJECTION leaf (owned-data slice 8, `33fdc58`).** An owned
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
- **Owned-collection FILTERED `.Count(pred)` (owned-data slice 9, `229294f`).** A *predicated* count over
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
longer: owned-data slice 9 (`229294f`) made that shape **native**, and closed EF-359 — see §3 and §6. The
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
  1. **Array projections — the WRAPPED spelling is DONE** (owned-data slice 8, `33fdc58`, 2026-07-30).
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

**JIRA STATE — reconciled 2026-07-31.** JIRA and this document had drifted apart; the sweep below has been
done, so the two now agree. Current state:

| Ticket | JIRA status | Why |
|---|---|---|
| EF-335, EF-357, EF-358, EF-359 | **`In Code Review`** | Fixed in code and reviewed, but on the **unmerged** stack |
| EF-360, EF-362, EF-365 | `Backlog` / `Needs Triage` | Genuinely still open |
| EF-375, EF-376, EF-377, EF-379 | `Needs Triage` (filed 2026-08-05) | Genuinely still open — filed by the joins slices, see below |
| EF-378 | `Needs Triage` — **should be closed as a duplicate of EF-375** | Measured to be the same defect; the transition has not been done |
| EF-322 (epic) | `In Progress` | — |
| EF-247 | `Blocked` | Check what it is blocked on before scheduling (§9.1 item 4) |

**`In Code Review`, deliberately, not `Closed`.** None of these fixes has shipped — the whole native stack is
unmerged — so closing them would assert something untrue and leave a `Closed` ticket with no fix version.
**They should be moved to `Closed` when the stack merges**; that step stays on the cutover checklist. Each
carries a comment recording the fixing commit, the mechanism, and explicitly why it is not closed yet.

**Two summaries were CORRECTED, because they stated root causes that were subsequently measured false** — this
matters beyond tidiness, since a reader who goes to JIRA first would otherwise be misled by the ticket title
itself:

- **EF-360** was *"Anonymous projection with an entity-collection leaf throws `ArgumentException` in every
  query mode"*. Slice 8 made exactly that shape native. Now: *"Projection with a collection leaf whose
  ELEMENT TYPE has its own eager-loaded navigation…"*, matching the row below.
- **EF-358** was *"Projection path materializes null…; whole-entity materialization normalizes to an empty
  list"*. There is no such split — nothing normalized on any path pre-fix, and the apparent normalization was
  CLR field-initializer masking. Now: *"A missing or explicitly-null embedded array materializes as null
  instead of an empty collection"*.

**The five tickets filed by the joins slices (2026-08-05).** All are on the unreleased native join path — at
`v10.0.2`/`v9.1.2`/`v8.4.2` any cross-collection `Include`/`Join` throws, `TranslateJoin` is `=> null`, and
`LookupExpression`/`_lookup_` do not exist in `src/` — so none is a breaking change and none needs a
`BREAKING-CHANGES.md` entry.

| Ticket | Defect | Symptom today |
|---|---|---|
| **EF-375** | Two joins onto the **same target entity type** collapse to one `_innerCollections` entry (`IEntityType`-keyed), so flattening never fires | Throws; **or silently wrong** — see §9.5 |
| **EF-376** | Lookup aliases are **navigation-name-only** and `AddLookup` de-dups on `As`, so sibling `ThenInclude`s collapse into one `$lookup` | Declines cleanly |
| **EF-377** | A chained `Join` whose first hop has **no model navigation** has no identity to scope the second hop under | Declines cleanly (was silently 0 rows pre-EF-372) |
| **EF-378** | **Duplicate of EF-375.** Filed as "two sibling reference `Include`s without `ThenInclude`" | See EF-375 |
| **EF-379** | A root navigation to a transitive hop's **target type** misroutes the hop into the root-level branch | **Silently wrong data** — see §9.5 |

A measured spike on 2026-08-05 **refuted** the natural hypothesis that these share one root cause. Seven
decision sites are involved and the tickets do not map one-to-one onto them; EF-375, EF-376 and EF-379 are
mutually independent, proved by instrumentation. "Key by navigation path" is a coherent *direction* but a
fiction as one fix, because **nothing in the IR records a path** — a `NavigationObjectAccessExpression` has
`parent = RootReferenceExpression` even for a hop-3 navigation — and path identity is reachable at every
*write* site and at no *read* site (`GetLookupAlias` is re-derived at ~10 read sites plus both bridge
resolvers). EF-377 is not in the family at all: it has *no* key, not a weak one.

Three ticket-level corrections worth knowing before picking any of them up, each measured:

- **EF-378 as filed is false.** Sibling reference `Include`s onto *different* target types work correctly. The
  precondition is the *same* target type; the absence of `ThenInclude` is incidental.
- **EF-379's stated fix direction is wrong.** The FK-*name* match tier misfires too on a name collision, so
  "prefer the FK-name match" is not the fix. The discriminator must be the FK's **receiver** — and not its
  member-chain depth either, since a genuine root hop was measured arriving as `Property(d.Outer.Outer, …)`.
- **All five tickets' line numbers are stale**, written against pre-rebase `34a02067`. The JIRA comments name
  sites by method instead.

This is the **third** time in this area that a separately-filed symptom turned out to be another doorway into
one defect — EF-366 via `GroupBy` and `Distinct`, EF-372 via `ThenInclude` and chained `Join`, and now EF-378
into EF-375. Worth assuming next time rather than discovering.

*Both tickets' original **descriptions** are left intact as the historical bug report; the correction lives in
a comment on each, which is where a reader will look for it.*

| Ticket | Type | Summary | Severity |
|---|---|---|---|
| **EF-353** | Task | Native bare owned-element SelectMany can't materialize **nested owned members** — currently a clean decline (`GetNavigations().Any()` guard); lifting it needs a re-rooted projection mapping | Feature gap, clean decline |
| **EF-354** | Bug | `SelectMany(o => o.Items, (o,i) => o)` (whole-outer, explicit method-call spelling) **crashes** ("Id missing") instead of declining cleanly; query-syntax spelling already declines | Loud crash, not wrong data |
| **EF-355** | Bug | Filtered reference SelectMany: folded-predicate split in `TrySplitCorrelation` can **silently drop a `!= null` inner filter** → returns all children | Silent wrong data, **latent/unreachable** today (EF emits nested, not folded, shape) |
| **EF-356** | Bug | Mixed whole-entity + computed-arithmetic projection (`new { c, Total = c.Age * c.Score }`) returns **silently wrong** values (`Score²`) — mixed shaper has no `BinaryExpression` handling | Silent wrong data, **pre-existing**, pinned by a documenting test |
| **EF-357** | Bug | **CLOSED** (`7c199e4`, 2026-07-29). Bare embedded-collection `.Count` projection (`Select(b => b.Posts.Count)`) threw `ArgumentException` in **every** query mode — a `MongoProjectionBindingExpressionVisitor` gap, not a native decline. Owned-data slice 7 (`0cb1b1b`) fixed the translation-time `ArgumentException` and made present arrays return correct counts, leaving a missing/explicitly-null-array `ArgumentNullException` at materialization as a residual (that residual was EF-358). The EF-358 fix closed that residual, so `Select(b => b.Posts.Count)` now returns correct counts for every array state | Was: hard fail every mode. Now: correct for every array state |
| **EF-358** | Bug | **CLOSED** (`7c199e4`, 2026-07-29). Root cause corrected during investigation — it is **not** a whole-entity-vs-projection split; pre-fix, nothing normalized a missing/explicitly-null embedded array on *any* path, and the apparent whole-entity normalization was CLR field-initializer masking (`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection` skips its fixup loop when `relatedEntities` is `null`). Fix: delete the null-collapse conditional from `BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case; add a `Coalesce` to an empty `BsonArray` at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case. Result is uniform, initializer-independent normalization on every path/mode/cardinality; closes EF-357's residual. See §4 and `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the full mechanism | Was: runtime throw / inconsistent shape. Now: closed |
| **EF-359** | Bug | **CLOSED** (`229294f`, owned-data slice 9, 2026-07-30). Filtered `Count(pred)` in a projection (`Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`) threw `InvalidOperationException` in **all three** query modes — a translation-time crash in `MongoProjectionBindingExpressionVisitor.Translate`, before `MongoQueryMode` is read; same shape of defect as EF-357, **not** the graceful decline earlier docs assumed. Mechanism of the fix, in one line: recognize the predicated `Count`/`LongCount` overloads by canonical `MethodInfo` at both the translator and the projection-binding sites, represent the result as a new sealed sibling node `MongoFilteredSizeExpression` (never a flag on `MongoSizeExpression`, so the Tier-1 array-index renderer, the query-dialect classifier and the negator all fail closed), and render `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`. The predicate spelling went native too; the bare spelling now folds client-side with correct values instead of crashing. Narrower residuals remain (§4), one of them filed as EF-365 | Was: hard fail every mode. Now: native, closed |
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

Measured by the two-sweep subtraction on the **EF10 specification suite**. **Fully re-measured at the current
tip `229294f` on 2026-07-31** — both sweeps re-run from scratch after slices 8 and 9 landed, with §7.1 and §7.2
re-derived from the fresh `nativeonly.trx` (previous revisions carried §7.2 forward from `1dd7862`). The
totals and every row of §7.1 reproduced the slice-7 figures **exactly**; §7.2 is restated below from the fresh
data. (Historical note retained: an earlier revision cited `7532b15` for the `.Count`-in-a-predicate slice;
that hash is not in the shipped history — it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`, and the squashed slice-6 commit is `1b4c1d6`.)

> `{ tests requiring driver-LINQ } = { pass under Native } − { pass under NativeOnly }`

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4589** | 0 | 19 | 4608 |
| `NativeOnly` (fallback removed) | **2194** | **2395** | 19 | 4608 |

Because `Native` fails **zero** tests, every `NativeOnly` failure was a `Native` pass — so the set is exact:

> **2395 spec tests currently require the driver-LINQ fallback to pass.** They go green under `Native`
> only by silently falling back; remove the fallback and they throw `NativeTranslationNotSupportedException`.

**Delta since the last measurement (slice 7, `ab886fa`): ZERO.** Slices 8 and 9 moved neither total, and the
per-class table is unchanged row for row. Before that, the only movement in the whole owned-data stream was
**2 tests** at slice 5 (`NativeOnly` 2192 → 2194 passing, 2397 → 2395 failing): both
`NorthwindAggregateOperatorsQueryMongoTest.Select_All` (`async: True`/`False`), which went native via the
EF-335 negator. Its `Native`-mode MQL baseline was re-baselined in that slice too — the driver fallback's
trailing `{ "$project" : { "_id" : 0, "_v" : null } }` stage disappears, which is the signature of native
routing. Results were unchanged.

So the figures above have now reproduced identically at **five consecutive tips** — `b087957` (slice 5),
`1b4c1d6` (slice 6), `ab886fa` (slice 7), and now `229294f` (slices 8 + 9 inclusive). Expected, and the reason
is structural rather than a coincidence to be re-investigated each time: **Northwind has no owned collections
or owned sub-property coverage**, so nothing in the owned-data work stream can touch these tests. See the
closing caution in §8 — a flat spec number here does not mean a slice achieved nothing.

**Both axes were checked per test on this sweep, not just the aggregate:** `Native` produced an **empty**
failure list (so no `Native`-mode MQL baseline moved — a baseline that had changed would surface as a failure
against its checked-in string), the two runs covered an identical 4608-test name set, and the per-class
`NativeOnly` breakdown in §7.1 was re-derived from the fresh `nativeonly.trx` and matched all 24 rows. Two
cautions for whoever re-measures next:

- **A total of 4608 is correct.** One intermediate measurement during slice 5 reported 4601 (7 low) and was
  wrong; the figure here reproduced exactly on a fresh base-vs-branch run and again on the final three-version
  sweep. Do not "correct" this table downward without a clean re-measurement.
- **Check both axes per test.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline that
  a slice changes — `Select_All` is exactly that. An inventory built only from the `NativeOnly` pass set will
  miss such flips (it missed this one).

Scope note: spec suite, EF10, at this tip. The functional `Native*` tests self-parametrize across modes so
they don't count here; unit tests don't touch a database.

### 7.1 By test class (24 classes)

*Re-derived from the fresh `229294f` `nativeonly.trx` (2026-07-31). Every one of the 24 rows below matched the
figure already in the table, summing to 2395 — this table is now confirmed at slice 7 and again at slice 9.*

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

**Re-derived from the fresh `229294f` sweep (2026-07-31), replacing the carried-forward `1dd7862` figures.**
The three *data* buckets (873 / 794 / 54) and the regex bucket (13) are **unchanged**; the rest shifted, and
the reconciliation is stated below the table rather than left as an apparent contradiction.

| Count | Cause |
|---:|---|
| 873 | **Non-entity projection not natively representable** — computed / scalar / client-eval projection long tail (`Select` shapes, casts, client methods) |
| 794 | **Query not natively representable** — joins, cross-collection navigation, non-native GroupBy shapes, misc operators |
| 559 | `Assert.Throws` **exception-type mismatch** — feature unsupported in *every* mode; test pins the *driver-LINQ* exception type, native throws `NativeTranslationNotSupportedException` instead |
| 66 | `ArgumentOutOfRangeException` (index) — a materialization / shaper gap surfaced only under `NativeOnly` |
| 54 | **Reference-nav `$lookup` not supported** — reference `Include` / navigation lookups |
| 26 | `Assert.Contains` — expected *error-message text* differs between the two throws |
| 13 | Non-constant regex pattern (EF-247) |
| 8 | Predicate renderer gap (`Not` over unsupported subtree) |
| 2 | `Throws_on_concurrent_query_first` — an MQL-string assertion (`"Customers."` vs `"Customers.{ "$limit" : 1 }"`), not a translation gap at all |
| **2395** | **total** |

Reconciling with the previous revision's numbers, so the differences are not mistaken for regressions:

- **`Assert.Throws` 507 → 559 and `Assert.Contains` 74 → 26** is almost certainly a *classification* difference,
  not a behavior change: this sweep's script buckets on `Assert.Throws` **first**, so a failure whose message
  contains both markers now lands in the `Assert.Throws` row. The pair sums 581 → 585, and the 4-test
  difference is accounted for by the 2 newly-separated `Throws_on_concurrent_query_first` rows plus the 2-test
  `Not`-renderer drop. Treat the *pair* as stable, not each row.
- **`Not` renderer 10 → 8** is real and is the expected direction: owned-data slice 5 taught `RenderUnary` to
  render `Not` over a query-native comparison. The bucket did **not** empty, by design — a `Not` whose operand
  is a conjunction or a nested `Not` still declines.
- The 2 `Throws_on_concurrent_query_first` rows were previously absorbed into an assertion bucket and are
  broken out here because they are categorically different: nothing about them is a coverage gap.

### 7.3 The fallback set splits into two meaningfully different kinds

*Figures below are the fresh `229294f` counts (previously stated as "~1744 / ~647" from the `1dd7862` sweep).*

- **1742 need driver-LINQ for correct *results*** (the `NativeTranslationNotSupportedException` data buckets:
  873 + 794 + 54 + 13 + 8). These are the genuine coverage gaps — remove the fallback and the user gets an
  exception instead of data. **This is the number that has to reach zero, or a deliberately accepted
  remainder, before driver-LINQ can be retired without regression** — see §9.
- **651 differ only in *failure shape*** (559 exception-type + 26 message + 66 index). These features are
  unsupported in *every* mode (no correct data is produced either way); the tests pass under `Native` only
  because the override pins the *driver-LINQ* exception type/message. Strictly they "require driver-LINQ to
  pass as written," but they aren't lost functionality — at parity cutover these overrides get re-baselined
  to assert the native exception. **This is bookkeeping at cutover, not coverage work**, but at 651 tests it
  is large enough to schedule deliberately rather than discover.
- **2 are neither** — the `Throws_on_concurrent_query_first` MQL-string assertions.

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

**CORRECTED at this revision — this paragraph used to read "The remaining native work is SP7 Phase 2
(streaming breadth: reducer/aggregate, collection-Include arrays, reference-Include) plus the parity cutover
that retires driver-LINQ." That is true only if "the parity cutover" is read as a single line item, which
badly understates it.** §9 (new) enumerates what parity actually requires, and the headline is that the
remaining work is dominated by three things this sentence did not name: **joins** (no native form at all),
**reference `Include`**, and the **computed/client-eval projection long tail** — together the bulk of the 1742
spec tests that still need driver-LINQ for correct results. SP7 Phase 2 is real but is **performance, not
parity**, and does not gate the cutover. One further item is in no plan yet: the EF9+ bulk
`ExecuteUpdate`/`ExecuteDelete` path shares the driver-LINQ bridge (§9.2). **The next work is joins, beginning
with reference `Include` — see §9.8 for the settled execution order, and §9.2 for the EF-317 ruling that
unblocked it.**

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

Empirically, 2395 EF10 spec tests still lean on the driver-LINQ fallback — **1742** for correct results (real
coverage gaps) and **651** only for the expected exception shape (re-baselined at cutover), plus 2 that are
neither. *(These were "roughly 1744 / ~647" from the `1dd7862` sweep; the figures here are the fresh
`229294f` re-measurement — see §7.2's reconciliation, and note the shift is almost entirely bucket
classification, not behavior.)* That number has **not** moved at all across slices 5–9, and for a good reason
worth remembering: **Northwind has no owned collections**, so this work stream's coverage gains are proven by
the functional `Native*` suites, not by the spec scoreboard. A flat spec number does not mean a slice achieved
nothing — but, per §9, it does mean the owned-data stream is not on the cutover's critical path.

---

## 9. What must be done before driver-LINQ can be retired without regression

*Added 2026-07-31, grounded in the fresh `229294f` two-sweep measurement (§7) and a source sweep of the
delegation surface. This section is the answer to "what is left", and it is deliberately separate from §5
("deferred items"): §5 lists what the epic still wants; §9 lists what **blocks deleting the fallback**. The two
are not the same set — several §5 items are optional at cutover, and several §9 items appear nowhere in §5.*

**The one-line answer.** Native cannot replace driver-LINQ today, and the binding constraint is **not** the
owned-data long tail that the last nine slices worked on. It is four things the native translator cannot
express at all — **joins, client-evaluated projections, reference `Include`, and `VectorSearch`** — plus one
structural dependency nobody has had to think about yet: **the EF9+ bulk `ExecuteUpdate`/`ExecuteDelete` path
uses the driver-LINQ bridge**, so "delete the fallback" does not currently mean "delete the bridge".

### 9.1 Coverage — the 1742 tests that need driver-LINQ for correct *results*

Ordered by size. Each is a genuine "remove the fallback and the user gets an exception instead of data".

| # | Gap | Spec tests | Notes |
|---|---|---:|---|
| 1 | **Non-entity projection long tail** | 873 | String transforms (`ToUpper`/`Substring`), date-part extraction, `Math.*`, type-changing casts, integer-result `Divide` (Guard A), **client-evaluated projections**, and the SP3-wide **bare-scalar projection boundary**. The single largest bucket, and the one with the most internal variety — it is not one feature. |
| 2 | **Query not natively representable** | 794 | Dominated by **`Join`/`GroupJoin`/`LeftJoin`, which have no native form whatsoever** (`TranslateJoin*` declines unconditionally); plus cross-collection navigation, the non-native `GroupBy` shapes (computed keys, computed accumulator operands, bare `GroupBy` terminating on `IGrouping`, user `resultSelector`, post-group HAVING/paging, correlated keys), and misc operators (`Contains`/`ElementAt`/`Last`). |
| 3 | **Reference `Include`** | 54 | The `$lookup`/`$unwind` machinery is **built but dormant** — it nav-expands to a `LeftJoin` the gate treats as non-native. Also blocks SP7 Phase 2's reference-Include streaming. Nested/transitive `ThenInclude`, filtered `Include` and collection-of-collection `Include` sit behind it. |
| 4 | **Non-constant regex** | 13 | **EF-247**, and note its JIRA status is `Blocked`, not merely open — check what it is blocked on before scheduling. |
| 5 | **`Not` over an unsupported subtree** | 8 | `RenderUnary` handles `Not` over a query-native comparison (slice 5); a `Not` whose operand is a conjunction or a nested `Not` still declines by design. Smallest and most self-contained item on this list. |

**`VectorSearch` is a large, concrete slice of the two big buckets — measured, not estimated, and this
corrects a claim made in the first draft of this section.** That draft said VectorSearch's 112 tests "land in
the exception-shape bucket rather than the data buckets", which would have made them cutover bookkeeping. The
opposite is true. Attributing the 112 `VectorSearchMongoTest` + `VectorSearchExactMongoTest` failures by
message:

| Bucket | VectorSearch tests |
|---|---:|
| "Query not natively representable" (row 2 above) | 82 |
| "Non-entity projection" (row 1 above) | 24 |
| `Assert.Throws` exception-shape | 6 |
| **total** | **112** |

So **106 of the 112 are inside the 1742**, and VectorSearch alone accounts for roughly **10% of row 2** and
**3% of row 1**. It is the largest single *named feature* in the coverage gap, and it is architecturally
distinctive: `VectorSearch(...)` is lifted out of the tree by the preprocessor *before* nav-expansion, so it
never reaches slot population at all — the gate declines it via `ContainsVectorSearch` over the captured
expression. Making it native means getting the lifted call back down to the lowerer as a `$vectorSearch` stage.

**Not in the table at all, because Northwind does not cover it — genuinely invisible to the counts above:**

- **Non-TPH `OfType`.**
- **The owned-data residuals in §5** (bare array projection, `OwnsOne`-hop array leaf/EF-362, correlated
  element predicates, two-scope owned quantifiers, owned-collection intermediate hops in dotted paths).
  Individually small; collectively the tail that the last nine slices have been eating.
- **Composite-PK member access is not native at all** — `MongoExpressionTranslator.TryResolveMember` and
  `TryResolveOwnedFieldPath` both decline a property that is a component of a composite primary key, because it
  is stored nested under `_id` and is not addressable by its own top-level element name. Resolving `_id.<name>`
  dotted paths is the fix. This is a documented strict-spike-parity decision from SP1, not an oversight — but
  it is a coverage gap that must be closed or consciously accepted before the fallback goes.
- **`Contains`, `ElementAt`/`ElementAtOrDefault`, `Last`/`LastOrDefault` have no binder at all** — they reach
  `NativeSlotPopulator`'s catch-all. Small, self-contained, and easy to overlook precisely because they decline
  so cleanly.
- **A set operation combined with an `Include` declines** — `IsPlainWholeEntitySelect` and
  `IsPlainProjectedSelect` both require **zero lookups**, so any `Union`/`Concat`/`Intersect`/`Except` over a
  source carrying an `Include` falls back (Union/Concat) or hard-fails (Intersect/Except).

### 9.2 The structural blocker nobody has scheduled: the bulk path shares the bridge

`MongoEFToLinqTranslatingExpressionVisitor` — the EF→driver-LINQ bridge, ~1050 lines plus a ~726-line
`LeftJoin` partial — is **not** used only by the query fallback. On **EF9 and EF10** it is also used by the
bulk `ExecuteUpdate`/`ExecuteDelete` translation, inside the `#if !EF8` region of
`MongoShapedQueryCompilingExpressionVisitor.cs`: `BuildIdDocumentQuery` (:1028), `BuildFilter` (:1049) and
`RenderSelfReferencingValue` (:1166) each construct one to lower a predicate body into `Mql.Field` form.

Consequence, stated plainly because it changes the shape of the cutover: **retiring the query fallback does not
retire the bridge.** Either the bulk filter/update translation is rewritten onto the native
`MongoExpressionTranslator`/`MongoQueryLanguageRenderer` first, or the bridge survives cutover as bulk-only
infrastructure. That is a real sub-project, and it is not currently on the SP scoreboard in §2.

**A second coordination question existed here — EF-317 — and it is now SETTLED. Owner ruling, 2026-07-31.**
Four `TODO(EF-317)` markers sit in exactly the code this epic wants to delete
(`MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs:37`,
`MongoProjectionBindingExpressionVisitor.Lookup.cs:34`, `MongoQueryExpression.Lookup.cs:23`). EF-317 is
`In Progress` and titled *"Use native driver LeftJoin to replace the cross-collection `$lookup` workaround"* —
i.e. it invests in the **driver-LINQ** join path, the same ground EF-322 must take natively (§9.1 items 2
and 3). This document previously recorded that as an open decision blocking the join family.

**The ruling: EF-317 is essentially THROWAWAY. None of its code needs to carry over to the native
implementation unless doing so happens to make sense. Build what joins need and do not design around EF-317.**
Coverage parity — every case EF-317 handles also being handled natively — is a **nice-to-have, not a
constraint**; EF-317 has not been merged long enough for its coverage to be worth protecting. Do not re-open
this as a blocker.

**One distinction the ruling does NOT collapse, and it matters:** "EF-317 is throwaway" applies to its
**`LeftJoin` partial** (`…LeftJoin.cs`, ~726 lines). The **main bridge file**
(`MongoEFToLinqTranslatingExpressionVisitor.cs`, ~1050 lines) is a *different* dependency — the EF9+ bulk path
above uses it, and that is unaffected by anything decided about EF-317. Retiring EF-317's join work does not
retire the bridge.

### 9.3 Test-suite work at cutover — 651 re-baselines, and 10 that cannot be re-baselined

- **651 tests differ only in failure shape** (559 exception-type + 26 message + 66 `ArgumentOutOfRangeException`).
  These features are unsupported in *every* mode; the tests pass under `Native` only because the override pins
  the *driver-LINQ* exception type or message. At cutover each gets re-pointed at the native exception. This is
  mechanical but large — schedule it, don't discover it.
- **The 66 `ArgumentOutOfRangeException` rows deserve a second look before being filed as bookkeeping.** They
  are described as a materialization/shaper gap surfaced only under `NativeOnly`. If any of them is really a
  native shaper bug rather than an expected decline, it belongs in §9.1, not here. Not re-investigated at this
  revision.
- **10 spec cases (5 classes) cannot be re-baselined by the usual instrument.** They assert EF Core's *upstream*
  `AssertTranslationFailed`, which requires an `InvalidOperationException` carrying "could not be translated" —
  an exception type currently produced *by the driver-LINQ fallback failing*. `EF_TEST_REWRITE_BASELINES`
  rewrites MQL baselines, not exception assertions. Two of the ten are EF8/EF9-only
  (`NorthwindFunctionsQueryMongoTest` is wholly `#if EF8 || EF9`), so **eight** exist on the EF10 axes. This
  constraint is already recorded as a hard constraint in `Query/AGENTS.md`'s array-valued-projections note; it
  is repeated here because it is a *cutover* constraint, not a slice-8 one.
- **The mode axis itself disappears.** `MONGODB_EF_NATIVE_ONLY=1` (`MongoTestStore.AddProviderOptions`) becomes
  vacuous, and ~24 functional `Native*` test files that self-parametrize over `MongoQueryMode` lose their
  `Native == DriverLinq` parity leg — which is, today, the primary oracle for a large amount of this work. **What
  replaces that oracle needs deciding before the fallback goes, not after.** The in-memory differential oracle
  (used by the `All`, `.Count` and filtered-count slices, where no driver oracle existed) is the obvious
  candidate and is already proven in this codebase.

### 9.4 Public API decisions to make (each is a breaking-change judgement)

Retiring the fallback makes the following public surface meaningless. Per `AGENTS.md`, breaks are measured
against the latest *released* assembly, so each needs a deliberate call — **note this cuts both ways: several
of these were added during this unreleased cycle and can simply be removed, but that must be verified per
member against the release tag, not assumed.**

- `public enum MongoQueryMode` (`Infrastructure/MongoQueryMode.cs`) — `DriverLinq` becomes meaningless and
  `NativeOnly` becomes a no-op. Removing the enum or a member is a source+binary break.
- `MongoDbContextOptionsBuilder.UseQueryMode(MongoQueryMode)` — `virtual`, so also a subclassing break.
- `MongoOptionsExtension.QueryMode` / `WithQueryMode(...)`, and the observable output of
  `GetServiceProviderHashCode`, `ShouldUseSameServiceProvider`, `PopulateDebugInfo`'s `"Mongo:QueryMode"` key,
  and `LogFragment`'s `QueryMode=…`.
- `MongoQueryCompilationContext`'s mode-bearing primary constructor and its `QueryMode` property; the
  `MongoQueryCompilationContextFactory` 2-arg constructor. **Precedent worth heeding:** that factory's 1-arg
  constructor already carries an explicit "do not delete, v10.0.2 compat" remark — this repo has already
  treated constructor removal here as breaking.
- `public record MongoExecutableQuery` — the one public type whose *shape* is driver-LINQ: its `Query`
  (a driver-LINQ expression) and `Provider` (`MongoDB.Driver.Linq.IMongoQueryProvider`) members become dead,
  while every live member (`NativePipeline`, `Session`, `Streaming`, `OutputSerializer`) is `internal`.
  Changing the positional record parameters breaks anyone constructing or deconstructing it. It is also the
  **only** place `MongoDB.Driver.Linq` leaks into public API.

`NativeTranslationNotSupportedException` is `internal sealed`, so the exception users would start seeing is
*not* currently public — decide whether it should be before it becomes the only failure mode.

### 9.5 Correctness debt that should not survive cutover

Cutover removes the safety net that currently makes some of these benign, so they change priority:

- **EF-356** — mixed whole-entity + computed-arithmetic projection returns **silently wrong** values
  (`Score²`). Reachable today under default `Native`. Silent wrong data is the worst category here.
- **EF-355** — filtered reference SelectMany can silently drop a `!= null` inner filter. Latent today (EF emits
  the nested, not folded, shape), but it is a silent-wrong-data mechanism sitting in code the cutover makes
  load-bearing.
- **EF-354** — whole-outer `SelectMany` crashes instead of declining cleanly.
- **EF-379** — a root navigation to a transitive hop's target type misroutes the hop, returning a **silently
  null navigation** where real data is correct. Reachable today under default `Native` *and* under explicit
  `DriverLinq`. **Row count does not discriminate it** — both the correct and the broken pipeline return the
  same number of rows; only the navigation *value* differs, so any regression test must assert the value. The
  trigger is an ordinary model shape: a root with a direct navigation to some type plus a longer chain that
  also reaches that type. **Recommended as the next slice** (§9.8).
- **EF-375** — two joins onto the same target entity type. Three symptom classes from three different sites: a
  throw (bare same-typed pair), a **silently null navigation** (add any third distinct-typed join and
  flattening fires, but the retroactive registration picks a navigation by target type alone — the pipeline
  then contains a `$lookup` for a navigation the query never mentions), and **silently wrong values** (a double
  join projecting both entities returns `TARGET-B|TARGET-B` for `TARGET-A|TARGET-B`, because
  `UsesDriverJoinFields ? "_inner" : …` discards the navigation and both projections read the same field).
  A fix addressing only the first site converts a throw into silent wrong data. Frequency is higher than it
  looks: `Employee.Include(Manager).Include(Mentor)` is enough, and **same-typed siblings are guaranteed by
  construction on any self-referencing model**.
- **EF-360, EF-365** — hard-fail in every mode where a graceful path is available. EF-365 in particular is
  *measured* to become a working fallback if one guard is deleted; after cutover there is no fallback to become.
- **The interposed-operator family** (`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat` between an
  owned-collection `Select` and a terminal operator) — recorded as a comment on the EF-322 epic rather than its
  own ticket. Hard-fails in every mode today.

### 9.6 Housekeeping that is cheap now and annoying later

- **JIRA is now reconciled** (done 2026-07-31 — see the top of §6), but **one step is deferred to merge**:
  EF-335/EF-357/EF-358/EF-359 sit at `In Code Review` and must be moved to `Closed` when the stack lands.
- ~~**The stack is 1 commit behind `upstream/main`**~~ — **done.** Rebased onto `58e05a0e`; `upstream/main` is
  now an ancestor of the tip. Note the rebase invalidated every SHA cited in §2 — see the warning in the header.
- **EF-378 should be transitioned to `Closed`/duplicate of EF-375**, and EF-377 retitled toward "a
  navigation-less join hop has no identity to scope under" so its distinction from EF-375/376/379 is visible
  from the issue list. Both are recorded as JIRA comments; neither transition has been done.
- **SP7 Phase 2** (§5) — reducer/aggregate streaming, collection-Include array streaming, reference-Include
  streaming (blocked on 9.1 item 3), and deleting the now-unreachable `RawBsonDocument` branch + `BsonRowReader`.
  This is performance, not correctness: it does **not** block cutover, and should not be allowed to.

### 9.7 The honest summary

Ranked by what actually gates the cutover:

1. ~~**Joins** (no native form at all) and **reference `Include`** — the two largest structural absences.~~
   **Partly closed as of 2026-08-05** — single-level reference `Include` is native (EF-368) and transitive-hop
   scoping and interleaved-operator positioning are fixed (EF-372, EF-373). What remains of this item:
   `ThenInclude` breadth, filtered `Include`, collection-of-collection, the general join, and the five defects
   in §6. This ranking was written when the answer was "nothing"; it is now "partial", so re-rank against
   item 2 before treating it as still first.
2. **The computed/client-eval projection long tail** — the largest bucket by test count, and the least
   unified: it is many small features, not one.
3. **`VectorSearch`** — **106 tests, promoted here on measurement.** It was ranked below `GroupBy` in this
   list's first draft on the assumption its tests were exception-shape bookkeeping; they are not, and it is the
   largest single named feature in the coverage gap.
4. **The bulk-path bridge dependency** (§9.2) — small in code, but currently unscheduled and easy to miss.
5. **Non-native `GroupBy` shapes**, then the small renderer/regex/`Contains`/composite-PK items.
6. **Test re-baselining and the public-API decisions** — not hard, but 651 tests and ~8 API members do not
   happen in an afternoon.

### 9.8 Execution order (settled 2026-07-31 — and NOT the same as §9.7)

**STATUS 2026-08-05 — step 1 is substantially delivered, and there is an OPEN QUESTION before step 2.**

Step 1 has shipped six slices (§2's joins table), through EF-373. What it also produced is five defects in the
same area (§6), of which **EF-379 is silent wrong data reachable from an ordinary model**. A measured spike
recommends inserting **EF-379 as a small standalone slice ahead of step 2 (`VectorSearch`)**, on these grounds:
it is the only one of the five that is silent wrong data *with a correct answer already reachable* (the same
query with the decoy navigation unmapped takes the transitive branch and returns correct rows, so the
destination path already works); it needs **no new IR**, unlike EF-375/EF-376; it has the smallest blast radius
of the five (one method plus one threaded argument — no alias changes, no shaper read sites, no bridge changes);
and it makes EF-375 easier by routing more hops through the resolver that declines rather than guesses.

**This insertion has been recommended but NOT ruled on by the owner.** Do not treat it as agreed — and do not
silently follow the un-amended order below either. Surface the question.

The recommended order *within* the five, if they are taken up: **EF-379**, then **EF-375** (all three of its
sites; this also closes EF-378, and it must **delete the agreement check** in
`TryResolveIntermediateLookupPrefix` — that check carries a `TODO(EF-375)` and exists only to paper over the
same imprecision, so its removal also requires re-baselining
`Two_same_typed_navigations_second_branch_only_declines_cleanly`, a test that currently pins a *decline* for a
query that should return correct rows), then **EF-376** — and EF-376 only *after* EF-373's shared lookup
resolver, since that resolver is the seam a path-scoped alias scheme has to change in lockstep. **EF-377 is
de-linked** from the group: it has no key rather than a weak one, and its clean decline is a reasonable resting
state.

---

§9.7 ranks by what **gates** the cutover. It is not an execution order and must not be read as one: it ignores
dependencies, and it was written while the EF-317 question was still open. With that question settled (§9.2 —
EF-317 is throwaway, build what joins need), the agreed order is:

1. ✅ **Joins, as one work stream — starting with reference `Include` as its FIRST SLICE, not as a detour.**
   *(Substantially delivered 2026-08-03…05: EF-366, EF-367, EF-370, EF-368, EF-372, EF-373 — see §2. Behind it
   still sit `ThenInclude` breadth, filtered `Include`, collection-of-collection, and the five filed defects.)*
   Reference `Include` and joins are the *same* `$lookup` machinery; reference `Include` is the constrained
   case (FK-correlated, single-level, left-join semantics) and is the one where the lowerer already carries
   built-but-dormant code behind a single gate site. Build it, then generalize. Starting at the general join
   is the higher-risk path for no compensating benefit.
2. **`VectorSearch`** — 106 tests, architecturally isolated, good parallel or follow-on work. *(It was ranked
   FIRST in the pre-ruling order for a reason that has since evaporated: it was the thing that could proceed
   while the EF-317 decision was pending. There is no longer a decision to wait on.)* Still worth a Task-0
   spike before committing — `$vectorSearch` must be the first pipeline stage, which cuts against the
   lowerer's canonical stage ordering.
3. **Projection long tail**, anchored on the **bare-scalar projection boundary** — one structural change
   unblocking bare scalars, bare entities and bare arrays together, rather than 873 individual fixes.
4. **The bulk-path bridge** (§9.2) — scope early, execute late.
5. **Test re-baselining + public-API decisions** — last.

**The owned-data work stream is not on this list.** Nine slices landed and it is *not* on the cutover critical
path — five consecutive tips of zero spec delta, structurally, because Northwind has no owned collections. Do
not default back to it because the machinery is warm.

**Where each of these is actually gated, by decline site** — recorded so the next slice does not have to
re-derive it. Ordered by leverage (how much of the fallback set one site's removal would unblock), which is
*not* the same order as the ranking above:

| # | Gap | The gate |
|---|---|---|
| 1 | Reference `Include` / `ThenInclude` / filtered / collection-of-collection | `MongoSelectLowerer.cs` catch-all lookup arm — a **single** site; the `$lookup`/`$unwind` machinery is already built |
| 2 | Bare-scalar, entity-ref and mixed projections | `NativeProjectionBinder.TryPopulateNativeProjection`'s selector-body shape check, plus the gate's "projects a non-entity result" throw |
| 3 | Computed long tail | `MongoExpressionTranslator.TranslateNode`'s final `return null`, `TranslateOperand`'s cast guard, and `NativeProjectionBinder.TryTranslateLeaf`'s final `return null` |
| 4 | Joins | `NativeSlotPopulator`'s catch-all + `MongoSelectLowerer`'s join-coverage guard; **GroupBy+Join is the one *hard* decline** (throws under `Native` too, because the driver fallback returns silently-empty joins) |
| 5 | `VectorSearch` | `ClassifyNativeDisposition`'s `ContainsVectorSearch` branch — needs the preprocessor-lifted call to reach the lowerer as a stage |
| 6 | GroupBy breadth | five separate sites: computed keys and computed accumulator operands in `NativeGroupByBinder`, element/result selectors in `TranslateGroupBy`, bare `IGrouping`, and the post-group guards |
| 7 | `Contains` / `ElementAt` / `Last` | no binder at all — `NativeSlotPopulator` catch-all |
| 8 | Composite-PK member access | `MongoExpressionTranslator.TryResolveMember` / `TryResolveOwnedFieldPath` |
| 9 | Parameterized `StartsWith`/`EndsWith`/`Contains` | `MongoQueryLanguageRenderer`'s constant-only regex term (EF-247); also unblocks `!(Count > @param)` via the negator |
| 10 | Correlated element predicates in `Any`/`All`/`Count(pred)` | `ReferencesEnclosingScope`; needs `$expr`+`$filter`/`$anyElementTrue` for the quantifiers, a two-scope element translator for the count |

**One caution about reading that table.** Every row is a *decline site*, not a *feature estimate* — item 1 being
"a single site" means the gate is in one place, not that reference `Include` is a small job. The table is for
locating the work, not for sizing it.

The owned-data work stream that has occupied the last nine slices is **not** on this critical path — which is
exactly what §7's flat spec number has been saying all along, and is worth stating plainly rather than
re-deriving each slice.
