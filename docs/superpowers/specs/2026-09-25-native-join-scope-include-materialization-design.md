# Native join-scope whole-entity leaf: Include materialization — Design

**Ticket:** EF-322, Phase 2 Group B (root cause A2 — the materialization half of root cause A).

**Depends on:** the already-committed recognition fix (`d1107a77`, `6e0446a4` on
`EF-322-Native-LINQ-rebased`) and its design doc,
`docs/superpowers/specs/2026-09-24-native-join-scope-include-wrapped-whole-entity-leaf-design.md` ("root
cause A"). That doc's own "Why the materialization side needs no change" section is the thing this doc
corrects — read it first; this doc assumes familiarity with its terms (`TryBindProjection`, `InnerPrefix`,
`WholeRootDocumentPath`, "wrappedLeafJoin arm").

## Problem

Root cause A's fix makes `NativeJoinScopeProjectionBinder.TryBindProjection` correctly *recognize* an
Include-wrapped whole-entity join-scope leaf and stage the right native `$project` shape. It does **not**
make the query actually work end to end. Two functional tests exist today, promoted verbatim from that
plan's Task 3 brief, and both fail against a real MongoDB — not with `NativeTranslationNotSupportedException`
(the "not yet supported, falls back cleanly" signal this codebase treats as fine), but with wrong or
crashing materialization:

- `Whole_outer_entity_leaf_that_is_also_reference_included_goes_native` — `db.Owners.Include(o => o.Orders)
  .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total })`. Under `NativeOnly`, this throws
  `System.FormatException: Element '_lookup_Orders' does not match any field or property of class Owner`,
  from `BsonClassMapSerializer<Owner>.DeserializeClass`.
- `Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join` —
  `db.Owners.LeftJoin(db.Orders.Include(r => r.OrderLines), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })`.
  Under `NativeOnly`, this does **not** throw — it silently materializes `r` as `null` for every row,
  including rows that matched (measured: Alice's `Total` came back `null` instead of `10`).

Both tests are in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`, immediately
after `Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly`, currently **RED and left
in the file on purpose** (see the plan's SDD ledger,
`.superpowers/sdd/2026-09-24-native-join-scope-include-wrapped-whole-entity-leaf/progress.md`, Task 3 entry).

A third, **separate**, non-join-scope control test was written during investigation (not committed) —
`db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })`, no join at all — and it *compiles and
runs* but the Included `Owner` navigation comes back empty (`Owner.Name` blank) even though the aggregation
pipeline visibly runs a `$lookup`/`$unwind` for it. This proves wrapped-Include-with-`new{}` has at least one
more, **pre-existing and unrelated to join scope**, silent data gap in this codebase. See "Non-goals" below —
do not fix that gap under this ticket without separately scoping it.

## Root cause, precisely

There are three distinct visitor passes in the native pipeline that know about `IncludeExpression`, and the
join-scope wrapped-leaf arm interacts correctly with only one of them.

1. **`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`'s wrapped-leaf-join arm**
   (`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`,
   the `else if (IsSingleEligibleNativeJoinScope(...) && NativeJoinScopeProjectionBinder.TryBindProjection(...))`
   branch, ~line 574 in the current file). On success it folds the join's own shaper into the selector body
   (`ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body)`,
   ~line 613) and calls `BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, foldedJoinBody)`
   (~line 617), which calls **`BindResultMember`** (~line 3377) once per projection member.

   `BindResultMember` pattern-matches `foldedExpression is StructuralTypeShaperExpression shaper`. For an
   Include-wrapped leaf, `foldedExpression` is `IncludeExpression { EntityExpression: <the shaper>, ... }` —
   the fold correctly substitutes the join's shaper *inside* the `IncludeExpression`'s `EntityExpression` (EF
   Core's own `IncludeExpression.VisitChildren` makes `ReplacingExpressionVisitor` do this for free), but the
   wrapper itself survives, so the top-level type check fails. Execution falls through to
   `BindSelectManyMember(mongoQueryExpression, alias, valueExpression)`, which registers the **raw, unfolded,
   still-Include-wrapped `leafBody`** — not even a bare `MemberExpression`, an actual `IncludeExpression` node
   — into `mongoQueryExpression.Projection` under the member's own alias (`"o"` / `"r"`).

   This is the immediate, measured cause of both failures:
   - **Outer case**: the native `$project` correctly emits `"o": "$$ROOT"` (root cause A's fix staged this
     correctly). But the DOM read side (`MongoProjectionBindingRemovingExpressionVisitor`, see #3 below) ends
     up resolving alias `"o"`'s *value* generically — reading the raw `"o"` field verbatim and handing it to
     `BsonClassMapSerializer<Owner>` — and that field's actual BSON value **is** `$$ROOT`, i.e. the *entire*
     post-`$unwind` document, which still carries the join's own unwound `"_lookup_Orders"` field. `Owner`'s
     class map has no such member and no `IgnoreExtraElements`, hence the `FormatException`.
   - **Inner case**: the native `$project` emits the Inner leaf under its fixed `InnerPrefix`
     (`"_lookup_Orders"`), never under the member's own alias (`"r"`) — this asymmetry is deliberate and
     documented on `NativeJoinScopeProjectionBinder` ("Alias space", "do not fix this back to the member
     alias"). Because `BindResultMember` mis-registered the leaf under alias `"r"` instead, the DOM shaper
     looks for a top-level `"r"` field that was **never emitted**, finds nothing, and silently returns `null`
     — no exception, just a wrong (empty) `r` on every row, matched or not.

2. **`MongoProjectionBindingExpressionVisitor`**
   (`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs`). Its
   `VisitExtension`'s `case IncludeExpression` (line 507) is the mechanism the *original* design doc meant by
   "the collection Include's own `$lookup` registers later, unconditionally, during native shaper/
   projection-binding compilation" — for a collection navigation it builds a `LookupExpression`, calls
   `_queryExpression.AddLookup(lookup)` (line 578), and rewrites the node via
   `RewriteCollectionIncludeForLookup` (line 583) into something the compiler can actually reduce. This is a
   real, working mechanism — but it only runs when something calls this visitor's **`Translate(queryExpression,
   expression)`** entry point (line 62) over a tree containing the `IncludeExpression`. `Translate` sets
   `_queryExpression`, visits, and — critically — **unconditionally calls
   `_queryExpression.ReplaceProjectionMapping(_projectionMapping)`** at the end (line 68), which *replaces the
   query's whole projection mapping*.

   The wrapped-leaf-join arm **deliberately never calls `Translate`** — the comment at
   `BuildSelectManyResultShaper`'s call site says so explicitly ("the result shaper is built HERE, by index,
   rather than left to the generic `_projectionBindingExpressionVisitor` fold... `ApplyProjection` early-returns
   when `Projection` is already non-empty... the DOM shaper would die in `GetProjectionIndex`"). That
   deliberate bypass is *why* `BindResultMember`/`BuildSelectManyResultShaper` exist at all — but it also means
   the Include's own `$lookup`-registration-and-rewrite (item 2 above) **never runs** for this arm. Confirmed
   empirically: a preliminary attempt at unwrapping+re-wrapping the `IncludeExpression` in `BindResultMember`
   (see "Fix attempted and reverted" below) got past the `FormatException`/silent-null failures but then hit
   `System.ArgumentException: must be reducible node` at `LambdaCompiler.Compile` — i.e. an `IncludeExpression`
   node (a non-reducible `Extension` node by default) was still present, unprocessed, when the compiler tried
   to emit IL for it. Nobody had rewritten it into a reducible shape, because step 2's visitor never ran.

3. **`MongoProjectionBindingRemovingExpressionVisitor`**
   (`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`), the
   DOM-level, compile-time, "read this `BsonDocument` back into CLR values" visitor
   (`MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery` line ~832). It **also** has its own `case
   IncludeExpression` (line 471) — this one assumes the entity-level `IncludeExpression.EntityExpression` has
   *already* been reduced to a `BlockExpression` (it does `(Visit(includeExpression.EntityExpression) as
   BlockExpression)!`), i.e. it expects to run *after* step 2 has done its job (and after EF Core's own base
   entity-materializer injection, which is what actually turns a bare `StructuralTypeShaperExpression` into
   that `BlockExpression` shape in the first place). It is not itself broken; it is simply never reached in a
   working state for this arm, because step 2 never ran to prepare its input.

**Summary:** root cause A2 is not one bug but a missing integration — the join-scope wrapped-leaf arm's
by-design, `ApplyProjection`-avoiding, index-based shaper construction has no analogue of the Include
bookkeeping that the ordinary (non-bypassing) path gets for free. `BindResultMember`'s pattern-match failure
is the visible symptom; the absent `$lookup` registration/node-rewrite is the deeper gap.

## Fix attempted and reverted (informs the design below — do not repeat blindly)

During investigation, `BindResultMember` was patched to unwrap a leading `IncludeExpression` chain off
`foldedExpression` before the `StructuralTypeShaperExpression` check, rebuild the shaper exactly as before,
then re-wrap the rebuilt shaper back inside the same chain of `IncludeExpression`s (`Update(rebound,
originalNavigationExpression)`, innermost first) so the wrapper would still be visible to *something*
downstream. `IncludeExpression.Update(Expression entityExpression, Expression navigationExpression)` was
confirmed via reflection against `Microsoft.EntityFrameworkCore.dll` (10.0.0) — two constructors exist
(with/without `setLoaded`), `Update` takes the two expressions only.

This compiled and ran, but failed *worse* than before: `System.ArgumentException: must be reducible node`
during `shaperLambda.Compile()`. Per the analysis above, this is because re-wrapping alone does not do
anything on behalf of the missing `MongoProjectionBindingExpressionVisitor` registration/rewrite step — it
just moves the un-reduced node one layer deeper. **The change was reverted** (`git checkout` on
`MongoQueryableMethodTranslatingExpressionVisitor.cs`); nothing from this attempt is in the working tree.

## Design options for the real fix

Not prescribing one without a spike — pick after a short investigation task (see "Plan shape" below).

**Option A — factor out the collection-Include registration, call it directly.** Extract
`MongoProjectionBindingExpressionVisitor`'s `case IncludeExpression` collection-nav logic (lines 507–583:
build the `LookupExpression`, the `UsesDriverJoinFields`/flat-multi-lookup prefixing, `AddLookup`,
`RewriteCollectionIncludeForLookup`) into a method callable **without** going through `Translate`/
`ReplaceProjectionMapping`. `BindResultMember` (or the wrapped-leaf-join arm, before/after folding) would then:
unwrap the `IncludeExpression` chain, rebuild the inner shaper as today, call the extracted helper on each
wrapper (innermost first) to get back a reduced/rewritten node instead of a bare re-wrap, and use *that* as
the member's bound value. Reference (non-collection) `Include`s in the chain still fall through to whatever
`VisitExtension`'s non-collection branch does (`_includedNavigations.Push`/`base.VisitExtension` — check
whether that also needs extracting, or whether it's a no-op for this shape once the entity shaper itself is
correct).

**Option B — give `MongoProjectionBindingExpressionVisitor` a narrower, side-effect-free entry point.** Add a
method (e.g. `VisitIncludeSubtree(MongoQueryExpression, Expression)`) that sets `_queryExpression`
temporarily, calls `Visit` on just the rebound `IncludeExpression` subtree, and restores state **without**
calling `ReplaceProjectionMapping` — since the wrapped-leaf-join arm's own projection mapping must stay
exactly as `NativeJoinScopeProjectionBinder.TryBindProjection` and `AddToProjection` already built it. Lower
surface-area change than Option A but touches a widely-depended-on class; check every other caller of
`Translate` isn't relying on some side effect of the full method (the `_translatedRootExpression`/
`_projectionMembers` stack bookkeeping) that a partial entry point would skip incorrectly.

**Option C — reject, don't materialize.** If A/B prove too invasive for the value delivered, an alternative
is for `NativeJoinScopeProjectionBinder.TryBindProjection` to explicitly **decline** (not stage) a leaf that is
Include-wrapped with a *collection* navigation specifically (reference-only Include might still be safe — needs
checking against the "Non-goals" control-test finding below, since reference Include already has an unrelated
bug), falling back to the pre-root-cause-A behavior for that narrower case. This directly contradicts the
purpose of root cause A's own fix (which exists specifically to make this go native), so treat this as a
last-resort de-scoping, not a first choice — likely means renaming/narrowing root cause A's own fix rather
than adding new capability, and needs a conversation about whether that's acceptable before doing it.

Whichever option is chosen, re-verify against **both** of Task 3's tests (Outer + Include, and Inner + Include
under LeftJoin) — they exercise different code paths (Outer's `$$ROOT` staging vs. Inner's fixed-`InnerPrefix`
staging) and both must produce correct data, not just "doesn't throw."

## Scope boundaries (deliberately NOT covered by this fix)

- **Root Cause B** (`Include_with_complex_projection_does_not_change_ordering_of_projection`) — untouched,
  per the original plan; still not this ticket's job.
- **The non-join-scope wrapped-reference-Include data gap** found as a control test
  (`Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })` silently returns an empty `Owner`) is
  real but **out of scope** here — it doesn't touch join scope at all, and conflating the two risks a fix that
  "works" for the join-scope case by accident while leaving the simpler case broken, or vice versa. File it
  separately once someone confirms whether it's the same `MongoProjectionBindingExpressionVisitor`
  reference-Include branch (`_includedNavigations.Push(...)`/`base.VisitExtension`, line 586) misbehaving, or
  something else entirely (e.g. a driver-side reference-Include navigation-fixup step not running for a
  *wrapped* `new{}` projection the way it does for a bare `select o`).
- **Task 4 of the original plan** (regenerating `Include_reference_when_entity_in_projection` /
  `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`'s spec baselines) is
  **blocked** on this fix landing — do not attempt it against the current broken materialization path; the
  spec tests assert real data, not just MQL shape, and would either fail to compile (Outer-rooted spelling) or
  silently pass with wrong data recorded as a "baseline" (Inner-rooted spelling, the null-swallowing case),
  which would be worse than not touching them at all.

## Testing strategy

- **Unit**: extend `NativeJoinScopeProjectionBinderTests.cs` only if the chosen fix changes staging behavior
  (Options A/B shouldn't — they change what `BindResultMember` does with an already-correctly-staged leaf, not
  what gets staged). If Option C is chosen, unit tests need updating to assert the new decline.
- **Functional**: the two tests already committed to the plan and currently RED in `NativeJoinTests.cs`
  (`Whole_outer_entity_leaf_that_is_also_reference_included_goes_native`,
  `Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join`) are the acceptance
  criteria — they must go GREEN under `NativeOnly` with **correct data**, not merely "doesn't throw." Re-use
  the reflection-verified `IncludeExpression.Update` signature and the MQL-dump-via-`SpyLoggerProvider`
  technique from this investigation if further diagnosis is needed — see
  `GetLogMessagesByEventId(MongoEventId.ExecutedMqlQuery)` (plural — a query that issues more than one command,
  e.g. a split Include query, needs every command inspected, not just the first).
- **Regression control**: re-run the non-join-scope wrapped-reference-Include control test
  (`Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })`) after the fix — it should either still
  exhibit its own pre-existing (separate) gap unchanged, or, if the chosen fix happens to touch shared code,
  confirm it didn't regress further.
- **Spec**: Task 4 of the original plan, once unblocked — same four `NorthwindInclude*MongoTest` sibling
  classes, same two target test names, same `EF_TEST_REWRITE_BASELINES=1` regeneration process documented in
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`.
- **Regression sweep**: `MONGODB_EF_NATIVE_ONLY=1` full `~Query` filter across EF8/EF9/EF10 — same
  before/after methodology as root cause A's own plan, since `MongoProjectionBindingExpressionVisitor` and
  `BindResultMember` are both shared, widely-used code.

## Plan shape (for whoever writes the implementation plan)

1. **Spike task, not a leap to implementation.** Prototype Option A (most concretely scoped) against just
   `Whole_outer_entity_leaf_that_is_also_reference_included_goes_native` first — it's the simpler of the two
   (single, non-left, join; Outer/root leaf; `$$ROOT` staging). Get it GREEN with correct data before touching
   the Inner/LeftJoin case.
2. Only after the Outer case is solid, extend to the Inner/LeftJoin case
   (`Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join`) — check whether
   it needs the SAME fix or something additional (the `InnerPrefix` fixed-alias mechanism is a different
   staging path than `$$ROOT`, per root cause A's own "Alias space" remarks).
3. Chain-depth (`scope.Levels.Count > 1`) coverage, mirroring root cause A's own Task 2 approach — only after
   1–2 are solid at depth 1.
4. Unblock and complete the original plan's Task 4 (spec baseline regeneration).
5. Full `test-all` regression sweep before merge.
