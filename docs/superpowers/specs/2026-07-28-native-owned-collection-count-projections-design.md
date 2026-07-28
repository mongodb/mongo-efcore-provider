# Native owned-collection count projections + EF-357 — design

*Epic EF-322 (native LINQ query provider). Owned-data translator slice 7, following the owned-collection
`.Count`-in-a-predicate slice.*
*Branch `EF-322-owned-collection-count-projection`, stacked on the native tip `1b4c1d6`
(`origin/NativeQueryOngoing`).*
*Targets the existing ticket **EF-357** ("Bare embedded-collection `.Count` projection throws
`ArgumentException` in every query mode"). **As built, EF-357 is only PARTIALLY resolved** — the header
originally said "closes"; the translation crash is fixed, but a missing or explicitly-null embedded array still
throws `ArgumentNullException` at materialization (**EF-358**). No JIRA number was filed for the
native-projection half; the two bugs this slice measured were filed as **EF-358** (projection-path null) and
**EF-359** (filtered `Count(pred)` hard-fails in every mode). The pre-existing interposed-operator gap
(`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat` between an owned-collection `Select` and a terminal
operator) is recorded as a comment on the **EF-322** epic.*
*SUPERSEDED IN PART by EF-358 (2026-07-29): this document is left as-built below, but every "PARTIALLY resolved"
/ "residual" statement about EF-357 in this file is now stale — a follow-on slice closed the missing/null-array
residual and EF-357 is FULLY closed. That slice also found the root cause this document assumes (see §7's
bullet below) was WRONG: it is not a whole-entity-vs-projection split; nothing normalized on any
path pre-fix, and the "whole-entity normalizes" reading was a CLR field-initializer artifact of the measuring
probe's own model, not provider behavior. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s rewritten
note and `docs/native-query-status-EF-322.md` §4/§6 for the corrected story.*
*ALSO SUPERSEDED, in the EF-359 direction (2026-07-30): **EF-359 is now CLOSED** — owned-data slice 9 (branch
`EF-359`) made the filtered `Count(pred)` projection NATIVE, so every statement in this file describing EF-359 as
an open, unfixed hard-fail (notably §7's bullet) is a dated record, not the current disposition. This file's
characterization of the crash and its `$size`-over-`$filter` rendering prediction both held up. The test named
here as pinning the crash, `NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode`,
is now `Filtered_count_projection_now_goes_native_EF359`. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s
"EF-359 AS BUILT" note and `docs/native-query-status-EF-322.md` §3/§4/§6.*

---

## 1. Problem

Two shapes, related by subject matter but not by cause.

**(a) The bare count projection hard-fails in every mode — this is EF-357.**
`ctx.Blogs.Select(b => b.Posts.Count)` throws `ArgumentException` under `Native`, `DriverLinq`, and
`NativeOnly` alike. It is not a native decline that falls back and works; it produces no data at all, and it
did so on `main` long before this work stream began (the crash site dates to 2023–2024 — see §2). It is pinned
today by a documenting test that asserts the throw:
`NativeOwnedCollectionCountTests.Bare_embedded_collection_Count_projection_is_a_known_preexisting_limitation`
(`tests/…/Query/NativeOwnedCollectionCountTests.cs:442-461`). *(AS BUILT: that test name no longer exists — this
slice renamed it away, because the shape it documented changed. The name is kept here as written because §1
describes the PRE-slice state; the post-slice pins were
`Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays` and
`..._still_throws_for_a_missing_or_null_array`.)*
*SUPERSEDED (EF-358, 2026-07-29): `..._still_throws_for_a_missing_or_null_array` no longer exists either — a
follow-on slice renamed and inverted it to
`Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array`, asserting `0` rather than
`Assert.Throws<ArgumentNullException>`. Do not copy the old name or its throw assertion into new work.*

**(b) A count leaf inside a projection is not natively representable.**
`ctx.Blogs.Select(b => new { b.Title, N = b.Posts.Count })` does not emit a native `$project`. This is the
last owned-collection predicate/projection gap named as deferred by the preceding slice
(`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, the `.Count` note):

> **Deferred, precisely (separate slices):** […] `.Count` BARE in a PROJECTION
> (`Select(b => b.Posts.Count)` […] this is a pre-existing hard-fail, not a graceful decline)

and the arithmetic form that *does* go native today is explicitly recorded there as an unplanned incidental
widening, not a designed capability:

> an ARITHMETIC projection leaf containing a count (`Select(b => new { X = b.Posts.Count * 2 })`) now goes
> NATIVE too (an unplanned incidental widening: the count is recognized as an ordinary operand)

So the odd state this slice resolves is: `Count * 2` is native, `Count * 1` would be native, and a plain
`Count` is not — and a *bare* plain `Count` crashes.

**A caution about (b), carried into the spike.** The code reading behind this design says shape (b) lands in
the *same* crash as shape (a), not in graceful fallback — the projection-binding visitor recurses into the
count node either way. Both `Query/AGENTS.md` and `docs/native-query-status-EF-322.md` currently imply that
only the bare form hard-fails, and **no test pins the anonymous-wrapped form in either direction**. This is
an unverified claim in the existing documentation, and §6 gates the slice on measuring it rather than
inheriting it.

---

## 2. Root cause

The two shapes fail for genuinely different reasons, which is why they get different fixes.

**(a) is a shaper-build crash in code common to all three modes.**
`Visitors/MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` runs the projection-binding
shaper fold unconditionally at `:349-351`, at *translation* time. `MongoQueryMode` is not read until
`MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery` (`:167`), strictly later — so anything that
throws in the fold throws in `Native`, `DriverLinq`, and `NativeOnly` identically.

Inside the fold, `Visitors/MongoProjectionBindingExpressionVisitor.VisitMethodCall` resolves
`EF.Property(b, "Posts")` to a `CollectionShaperExpression` whose `Type` is the navigation's CLR type,
`List<Post>` (`Expressions/CollectionShaperExpression.cs:39-40`). `MatchTypes` (`:711-718`) declines to insert
a conversion whenever the target type has an item type, so the generic fall-through at `:466`
(`methodCallExpression.Update(null, newArguments)`) hands a `List<Post>` to
`Queryable.Count<Post>(IQueryable<Post>)`. BCL expression validation rejects it. **The throw is BCL, not
provider code** — there is no `ArgumentException` literal on this path.

Dating: `git log -L 455,470` on that file yields only commits from 2023-07-19 through 2024-04-30, all long
before `d04c0f2` (2026-07-26), the first EF-322 native commit. The "pre-existing" claim is verified, not
assumed.

**(b) is an ordinary native-representability gap.**
`NativeTranslation/NativeProjectionBinder.TryTranslateLeaf` (`:99`) has exactly three accept branches: a
field-ref member access (`:107`), the EF-339 *reference*-collection count binder (`:124`, which requires an
`EntityQueryRootExpression` and so can never fire for embedded data), and numeric arithmetic with a
`BinaryExpression` top node (`:135-141`). A plain count leaf matches none of them and falls to `:143`.

**Why the arithmetic form escapes the crash, and why that is the template for the fix.** With `Route` set to
`NativeRoute.Projection`, `MongoProjectionBindingExpressionVisitor.Visit`'s arithmetic case at `:165-170`
registers the whole `BinaryExpression` as **one** projection member and returns — the visitor never recurses
into the count node, so `:466` is never reached. The `Route == Projection` guard there is load-bearing for
exactly this reason.

---

## 3. Scope

**In:**

- **(I) EF-357.** `Select(b => b.Posts.Count)` stops crashing and returns correct results in every mode.
  Going native is explicitly *not* required of it — see §4.
- **(II) Native count leaf.** An anonymous-type / DTO projection whose leaf is an unfiltered owned-collection
  count emits a native `$project` leaf.

**Breadth of (II)**, mirroring what the `.Count`-in-a-predicate slice proved:

- `.Count` (property), `.Count()`, and `.LongCount()`.
- A count reached through one or more owned single-reference hops (`b.Home.Notes.Count`).
- A count leaf alongside other leaves in the same projection (`new { b.Title, N = b.Posts.Count }`).
- The existing arithmetic-containing form (`new { X = b.Posts.Count * 2 }`) keeps working, byte-identically.

**Out (each with its reason):**

- **A filtered count**, `Count(p => …)`. Expressible here — a projection leaf is always in the aggregation
  dialect, so `$size` over `$filter` is available, and the predicate slice's blocker (no `$expr` inside
  `$elemMatch`, no array-index form for a predicated count) does not carry over. Deferred anyway because it
  needs a new `$filter` rendering arm *plus* an element-scoped predicate translated into the aggregation
  dialect rather than the query dialect the quantifier work built — its own correctness surface, cheap to add
  as a follow-on once the plain leaf is proven.
- **Array-valued projections** — `Select(b => b.Posts)`, `Select(b => b.Posts.Select(p => p.Title))`, and
  their anonymous-wrapped forms. These decline gracefully and return correct results today via the mixed
  driver-LINQ shaper. Making them native requires a mechanism that does not exist: the DOM shaper has no way
  to read an array-valued leaf back from a `$project` alias. Its only array materializer
  (`MongoProjectionBindingRemovingExpressionVisitor.cs:134-189`) is navigation-driven, requires an
  `ObjectArrayProjectionExpression`, and always produces entities, never a list of scalars.
- **Bare-scalar native pushdown.** A bare-scalar terminal projection never populates `Projection` at all — a
  pre-existing SP3-wide boundary, not a count-specific one. Making the bare count native means either a
  single-shape carve-out in that boundary (which the next bare-scalar slice would have to reconcile) or
  widening bare-scalar pushdown generally (a substantially larger slice). Neither belongs here.
- **Reference-collection consolidation.** Reference-collection counts already go native via the EF-339
  `$lookup` + `$size` binder. Unifying the two paths would be a refactor, not new coverage.
- **Two-scope counts** inside a `SelectMany`, consistent with every other owned slice.

---

## 4. Approach

### 4.1 (II) — the native count leaf

Two additions, each with an exact precedent one case away, and **no new IR node, pipeline stage, or renderer
arm**.

**Binder.** `NativeProjectionBinder.TryTranslateLeaf` gains an accept branch between the field-ref branch and
the arithmetic branch: accept the leaf when it translates to a `MongoSizeExpression`, reusing the
`TryMatchCountExpression` + `TryResolveOwnedCollectionPath` pair already invoked from the operand position at
`MongoExpressionTranslator.cs:581-585`.

The gate keys on the resulting **node kind**, not on "some value translated". This is deliberate and mirrors
why the arithmetic branch insists on a `BinaryExpression` top node: a bare constant or parameter leaf renders
as `{X: 1}`, which `$project` reads as an *inclusion flag* rather than a literal. `{$size: …}` is a document,
so it is safe precisely where a bare value is not. Widening the gate to "any `TryTranslateValue` success"
would reintroduce that hazard.

**Shaper fold.** A leaf case gated on `_queryExpression.Select.Route == NativeRoute.Projection` registers the
count `MethodCallExpression` as one projection member — mirroring the arithmetic case. This is also what keeps
shape (b) away from the `:466` crash, by the same mechanism that protects `Count * 2` today. The `Route` guard
is load-bearing, not stylistic: a mixed or fallback shape must fall through unaffected.

**AS BUILT — the case is in `VisitMethodCall`, not `Visit`.** This section planned it in
`MongoProjectionBindingExpressionVisitor.Visit` (mirroring where the arithmetic `BinaryExpression` case lives);
as built it is in `VisitMethodCall` (~`:394`), and the placement matters rather than being incidental: `Visit`
runs ahead of `VisitMethodCall` for every method call, so a case there would also have intercepted the
reference-collection count that `TryBindProjectedCollectionNavigationCount` handles in `VisitMethodCall`
(EF-339/SP5's `$lookup` + `$size` path). Placing it in `VisitMethodCall`, after that binder, leaves the
reference-collection path reached first. The as-built comment on the block records the two ordering constraints
and which of them was measured load-bearing.

**AS BUILT — the inclusion-flag rationale two paragraphs above is refuted.** The gate paragraph says a bare
constant/parameter leaf "renders as `{X: 1}`, which `$project` reads as an *inclusion flag*". Measured after the
fact (gate relaxed to plain `TryTranslateValue` success): a truthy constant or captured parameter returns
**correct** values (folded client-side, junk field in the emitted `$project`), while `X = 0`/`X = false`
**aborts the command** — `Cannot do exclusion on field X in inclusion projection`. The conclusion (keep the gate
on node kind) is unchanged; see `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the full measurement.

**Rendering and read-back need nothing new.** `MongoAggregationExpressionRenderer`'s `MongoSizeExpression` arm
(`:60-64`) and `MongoPipelineFactory.RenderProject` (`:145-160`) already emit the leaf; the raw-by-alias scalar
read at `MongoProjectionBindingRemovingExpressionVisitor.cs:128-131` already reads it back. Both are exercised
today by the arithmetic form.

Emitted MQL for `Select(b => new { b.Title, N = b.Posts.Count })`:

```json
{ "$project": { "_id": 0, "Title": "$Title", "N": { "$size": { "$ifNull": ["$Posts", []] } } } }
```

### 4.2 (I) — the EF-357 crash fix

`MongoProjectionBindingExpressionVisitor.VisitMethodCall` gains a `Queryable.Count` / `Queryable.LongCount`
over a `CollectionShaperExpression` case beside the existing `Queryable.Select` case at `:438-453`, rebuilding
the node as `EnumerableMethods.Count` over the shaper — the same move that case already makes for `Select`.

**Why narrow rather than a root-cause repair.** The underlying defect is broader: `MatchTypes` (`:711-718`)
declines the conversion for *any* target type with an item type, so `Count`, `First`, `Any`, `Sum` and the rest
of the `Queryable.*` surface over a collection shaper are all reachable from the same `:466` fall-through.
Repairing `MatchTypes` would fix them together and is more honest about the cause — but it changes type
coercion on a path every projection in the provider walks, in all three modes, in code dating to 2023, and the
shapes it would newly unblock are untested and unscoped. The narrow case fires only on a shape that throws
today, bounding the blast radius to "shapes that are already broken". The root-cause repair is recorded here
as a follow-on, not adopted.

**Rejected alternative: decline cleanly instead of fixing.** Converting the crash into a clean
`NotSupportedException` is cheaper and would satisfy the versioning rubric (the exception type of an
unsupported shape is not contract), but it yields no data, and the bar set for this slice is correct results.

**Disjointness.** (I) and (II) cannot collide: when `Route == Projection`, (II) short-circuits in `Visit`
before `VisitMethodCall` is reached; the bare form never populates `Projection`, so `Route` is `Fallback` and
only (I) applies. This is disjoint by construction, and gets an explicit test rather than being assumed.

---

## 5. Correctness

**`$ifNull` is mandatory, not defensive.** `$size` evaluated against a missing or explicitly-`null` array is a
hard server error that aborts the whole aggregate command — not merely a wrong answer. The count leaf is
therefore built with `nullSafe: true`. `MongoSizeExpression.NullSafe` keeps its `false` default, so the EF-339
reference-collection `$lookup` path — where a `$lookup` always writes an array — stays byte-identical.

**AS BUILT — there is no new call site.** This section said "the new call site therefore passes
`nullSafe: true`". As built, no call site was added: the binder reaches the *existing*
`MongoExpressionTranslator.TranslateOperand` construction (`new MongoSizeExpression(arrayPath, node.Type,
nullSafe: true)`, ~`MongoExpressionTranslator.cs:584`, added by the `.Count`-in-a-predicate slice) through
`TryTranslateValue`, unchanged. That reuse is why this slice adds no IR node, no renderer arm, and no new
`nullSafe` decision.

**Semantics.** A missing, explicitly-`null`, or empty array all yield `0`. That matches what EF materializes
for a missing embedded array (an empty list) and what LINQ's `Count` returns for it, so no state needs
special-casing.

**The same-named-scalar hazard is handled structurally, and inherited unchanged.**
`TryMatchCountExpression` matches by *name*, so it must not be fooled by a mapped entity scalar property
literally called `Count`. The protection lives in `TryResolveOwnedCollectionPath`: the chain must be rooted at
the query parameter, have at least one hop, and its final hop must be an embedded **collection navigation** —
a mapped scalar's receiver is an entity, never a collection. This is *not* call-site ordering; the predicate
slice measured that explanation false (moving count recognition ahead of `TryResolveMember` turned no test
red) and the correction should not be undone here.

**Risk class.** Neither half can silently change a correct result into a wrong one:

- (II), if the spike confirms the reading in §1, is **crash → native** — there is no prior behavior to
  regress. If instead the shape turns out to fall back and work today, it becomes **fallback → native**, which
  *can* change results, and the verification bar shifts accordingly (§6).
- (I) is **crash → result** on a shape that produces nothing today.

---

## 6. Verification

### 6.1 Slice-0 spike (throwaway, gates the rest)

Three questions cannot be answered from code and must be measured before any production edit:

1. **Does the anonymous-wrapped count actually crash today?** Code reading says yes; the existing docs imply
   no; nothing tests it. The answer decides whether (II) is crash→native or fallback→native, and therefore
   whether parity assertions are required.
2. **What does the bare form do once unblocked** — count client-side over the materialized array, or does the
   driver's LINQ provider render `$size` server-side? This decides whether (I) is "no longer crashes" or "no
   longer crashes and is efficient", and it is the difference between a documented limitation and a closed one.
3. **Is there a driver-LINQ oracle for (II) at all?** `Query/AGENTS.md` records, from live measurement, that
   the driver renders a collection count as `$expr`/`$size` *without* `$ifNull` and that MongoDB throws
   `MongoCommandException` the instant it evaluates that against a missing or explicitly-null array. If that
   holds here, parity is available only on well-formed rows — the same two-seed situation the `Any` and `All`
   slices hit, and the tests must be structured for it up front rather than retrofitted.

The spike also pins the exact incoming expression-tree shape for each of `.Count`, `.Count()`, and
`.LongCount()`, rather than inheriting the assertion at `MongoExpressionTranslator.cs:918-923`.

### 6.2 Primary gate

The differential in-memory-oracle test the `All` and `.Count` slices established: the **same `Expression`
object** sent to the server and compiled for client-side evaluation, over an array-state matrix of
missing / explicit-null / empty / one / many, across `.Count`, `.Count()`, `.LongCount()`, a count through an
owned single-reference hop, and a count alongside a sibling leaf. Sending one expression to both sides is what
makes it a real differential test rather than two hand-written predicates that can silently diverge.

Routing is proven under `MongoQueryMode.NativeOnly` (succeeding), never by MQL shape alone.

### 6.3 Regression and documentation

- `NativeOwnedCollectionCountTests.Arithmetic_projection_leaf_containing_a_count_goes_native` must stay green
  with byte-identical MQL — the incidental widening must not be disturbed by giving the plain leaf a
  deliberate path.
- The EF-357 documenting test flips from asserting `ArgumentException` to asserting correct results. Its
  comment, the `.Count` note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, and the EF-357 row plus §4
  and §5 of `docs/native-query-status-EF-322.md` are updated **together** — that test exists precisely to make
  a behavior change here impossible to land silently.
- The `Query/AGENTS.md` statement that only the *bare* form hard-fails is corrected in place to whatever the
  spike measures, rather than rewritten.
- An explicit test for the (I)/(II) disjointness described in §4.2.

### 6.4 Sweeps

Three-version `/test-all` (EF8 / EF9 / EF10), zero failures. A `NativeOnly` spec sweep checked on **both**
axes — the pass set *and* `Native`-mode emitted MQL. Northwind has no owned collections, so the expectation is
zero delta on both; the two-axis check is not ceremony, it is the correction from slice 5, where a pass-set-only
inventory missed a flip (`Select_All`) that had a changed `Native`-mode MQL baseline.

---

## 7. Follow-ons this slice deliberately leaves open

- **Filtered count in a projection** — `$size` over `$filter`; expressible, scoped out above. **Corrected
  as-built (Task 4 measurement):** the framing above — "deferred for cost, not impossibility" — assumed this
  shape falls back gracefully today. It does not. `Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`
  throws `InvalidOperationException` identically under `Native`, `DriverLinq` **and** `NativeOnly`, at
  translation time inside `MongoProjectionBindingExpressionVisitor.Translate`, before `MongoQueryMode` is read.
  The `$size`-over-`$filter` *rendering* claim still stands; the graceful-fallback assumption does not. The
  follow-on is therefore a **bug fix of the same shape as EF-357**, filed as **EF-359**, and it must clear that
  translation crash before any native rendering is reachable. Pinned by
  `NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode`.
- **The projection path's missing/null-array normalization gap** (added as-built, filed as **EF-358**): the
  projection path materializes `null` where whole-entity materialization normalizes to an empty list. This is
  the residual that keeps EF-357 only partially resolved, and it blocks array-valued projections below.
  **SUPERSEDED (EF-358, 2026-07-29):** closed by a follow-on slice, and the root cause stated here is WRONG —
  it is not a whole-entity-vs-projection split. Nothing normalized a missing/explicitly-null embedded array on
  *any* path pre-fix; the apparent whole-entity normalization was a CLR field-initializer artifact
  (`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection` skips its fixup loop when
  `relatedEntities` is `null`, so a materialized navigation kept whatever the CLR class's own initializer left).
  Post-fix, normalization is uniform and initializer-independent on every path/mode/cardinality, and EF-357 is
  now fully closed. It does **not** unblock array-valued projections below — those remain blocked on the
  unrelated DOM-shaper mechanism named there. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s rewritten
  note for the full corrected mechanism.
- **The `MatchTypes` root-cause repair** (`MongoProjectionBindingExpressionVisitor.cs:711-718`), which would
  unblock `First`/`Any`/`Sum`/… over a collection shaper alongside `Count`.
- **Array-valued projections**, blocked on an alias-driven array read-back mechanism in the DOM shaper.
- **Bare-scalar projection pushdown**, which would subsume the bare count as one case and is the larger prize:
  the biggest single fallback bucket in the spec measurement is 873 tests attributed to "non-entity projection
  not natively representable".
