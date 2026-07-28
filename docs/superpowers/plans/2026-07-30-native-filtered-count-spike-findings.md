# EF-359 Task 0 spike findings — filtered `Count(pred)` over an owned collection

*Branch `EF-359`. Measured against the `NativeQueryOngoing` tip this branch stacks on (`33fdc58`).
Design doc: `docs/superpowers/specs/2026-07-30-native-filtered-count-design.md`. Brief:
`.superpowers/sdd/2026-07-30-native-filtered-count/task-0-brief.md`.*

No production code was touched. Two throwaway probes were written, run, and then deleted before this
document was committed:

- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef359SpikeProbeTests.cs` (steps 1, 2, 3, 5 —
  functional, against a live `mongodb/mongodb-atlas-local` TestContainers instance; both `MONGODB_URI` and
  `ATLAS_URI` unset).
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/Ef359SpikeProbeUnitTests.cs` (step 4 —
  pure unit test, no database).

All commands were run via `dotnet test ... -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>"`
after `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Docker was available; the standard
`mongodb/mongodb-atlas-local` container booted normally (server version reported in the Step 5 explain output:
`8.3.2`).

As a cross-check (not a substitute for the probe — see Step 1), two **pre-existing, already-committed** tests
in `NativeOwnedCollectionCountTests.cs` independently pin shapes A and B and were re-run to confirm they still
pass on this branch: `Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode` (shape A) and
`A_predicated_Count_declines_and_falls_back_to_correct_rows` (shape B). Both passed
(`Total: 2, Passed: 2, Failed: 0`). Their assertions agree exactly with this spike's own, independently-run
probe for those two shapes.

---

## Step 1: current failure/success for shapes A/B/C, all three `MongoQueryMode`s

**Run:** for each of shape A (`Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })`), shape B
(`Where(b => b.Posts.Count(p => p.Rank > 0) > 2)`), and shape C
(`Select(b => b.Posts.Count(p => p.Rank > 0))`), executed under `Native`, `DriverLinq`, and `NativeOnly` in
turn, against a `SeedLengths`-shaped collection (posts arrays of length 0–3, plus a `Posts`-missing row and a
`Posts: null` row — the same seed idiom `NativeOwnedCollectionCountTests` uses).

**Observed:**

| Shape | Native | DriverLinq | NativeOnly |
|---|---|---|---|
| **A** (projection) | Throws `System.InvalidOperationException`: *"The LINQ expression 'o' could not be translated. Either rewrite the query in a form that can be translated, or switch to client evaluation…"* | Same exception, same message | Same exception, same message |
| **B** (predicate) | **Succeeds** | **Succeeds** | Throws `MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException`: *"Query is not natively representable and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback."* |
| **C** (bare projection) | Throws the identical `InvalidOperationException` as A | Same | Same |

Top provider frame (identical for A and C, in all three modes):

```
at MongoDB.EntityFrameworkCore.Query.Visitors.MongoProjectionBindingExpressionVisitor.Visit(Expression expression)
    in .../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs:line 110
```

Reading that line (EF10 configuration): it is the `case ParameterExpression parameterExpression:` arm inside
`Visit` — `_collectionShaperMapping.ContainsKey(parameterExpression) ? parameterExpression : throw new
InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()))` — reached via
`Translate`'s `var result = Visit(expression);` (line 61). This is a small precision correction to the design
doc's wording ("the crash happens inside `MongoProjectionBindingExpressionVisitor.Translate`") — the throw site
itself is `Visit`, one frame inside `Translate`; the substance (translation-time crash, before `MongoQueryMode`
is read, mode-independent) is exactly as the design doc states.

Top frame for B's `NativeOnly` decline (a clean compile-time gate throw, not a crash):

```
at MongoDB.EntityFrameworkCore.Query.Visitors.MongoShapedQueryCompilingExpressionVisitor.ThrowIfNativeOnlyForbidsFallback(MongoQueryMode mode, String reason)
    in .../Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:line 724
```

**B's MQL under `Native`/`DriverLinq` (identical in both modes — this is the driver's own LINQ v3 rendering,
not a native pipeline):**

```
aggregate([{ "$match": { "$expr": { "$gt": [
  { "$sum": { "$map": { "input": "$Posts", "as": "o",
      "in": { "$cond": { "if": { "$gt": ["$$o.Rank", 0] }, "then": 1, "else": 0 } } } } },
  2
] } } }])
```

This ran to completion (no `MongoCommandException`) over the **full ragged seed**, including the
`Posts`-missing and `Posts: null` rows — no `$ifNull` is present anywhere in this pipeline, yet it did not
abort. The driver's `$sum`-over-`$map` rendering evidently tolerates a missing/null `input` (unlike a bare
`$size`, which is documented elsewhere in this codebase as aborting on exactly that shape) — `$map` over a
missing/null input apparently evaluates to something `$sum` reduces to `0` rather than raising an error. This
was not traced further (out of scope for T0), and — as originally written here — this "no abort" observation
was over-stated into a claim of full row-level correctness on ragged data, which this spike's original probe
had not actually checked (only that the command didn't throw, not which rows it returned). That gap was closed
by a **fix-round-1 follow-up probe**, below, which checks row-level agreement directly rather than inferring it.

**Fix-round-1 follow-up: row-level correctness of shape B on ragged data, measured directly.** A throwaway
probe (`Ef359SpikeProbeRound2Tests.cs`, deleted before commit, same conventions as the original probes) ran
`Where(b => b.Posts.Count(p => p.Rank > 0) > k)` under `DriverLinq` against the ragged seed (`len0`–`len3`,
`missing`, `null`), for `k = 0` and `k = 1`, and compared the returned **titles** against the in-memory oracle
(materialize under `Native`, then evaluate the same compiled `Expression<Func<Blog, bool>>` client-side) — the
exact idiom Task 5 plans to use. `k = 0` is the discriminating case: a ragged row's filtered count is `0`, and
`0 > 0` is `false`, so a wrong ragged-row count (e.g. `null`/an exception swallowed into `true`, or any non-zero
value) would show up as an extra or missing title at exactly this boundary.

Observed:

| `k` | in-memory oracle | `DriverLinq` | Agree? |
|---|---|---|---|
| 0 | `[len2, len3]` | `[len2, len3]` | **Yes** |
| 1 | `[len3]` | `[len3]` | **Yes** |

Both runs agree exactly, and in both cases `missing` and `null` are correctly **excluded** — the same way the
well-formed `len0` row (also filtered-count `0`) is excluded — not merely "not aborted, cause unknown." This
**measures**, rather than infers, that the driver's `$sum`/`$map` rendering treats a missing/null `Posts` as a
filtered count of `0`, in agreement with LINQ's in-memory semantics, at exactly the boundary where a
miscount would surface.

**B's disposition, resolved:** B does **not** crash — it declines cleanly under `NativeOnly` and returns
correct results (row-level, on well-formed **and** ragged data, per the follow-up above) via the driver-LINQ
fallback under `Native`/`DriverLinq`, confirmed both by this spike's own live probes and by the pre-existing,
already-committed test
`NativeOwnedCollectionCountTests.A_predicated_Count_declines_and_falls_back_to_correct_rows` (added in a prior
slice, commit `1b4c1d6`, and still green today — though note that test's own seed is `SeedWellFormed` and does
not itself exercise the ragged states; the ragged-row correctness claim rests on this spike's follow-up probe,
not on that pre-existing test). This **is** the fallback→native flip the design doc's §2 table describes for
shape B — confirmed, not contradicted. Per the brief's framing: since B already returns correct results via
fallback, its differential-oracle rows (Task 5) are **mandatory**, and — per the measured follow-up above —
they can safely span the full ragged matrix, not just well-formed rows: this is now a measured fact for the
two `k` values probed, not an inference from a non-abort observation. (Task 5 should still cover its own full
breadth of operators/thresholds; this follow-up establishes the *mechanism* is trustworthy at the one boundary
where it would most plausibly fail, not every row Task 5 will eventually need.)

**Shape C** was *not* previously pinned by an existing test (only A and B have pre-existing tests in
`NativeOwnedCollectionCountTests.cs`). This spike is therefore the first live measurement of C: it crashes
identically to A, with the same exception type, message, and top frame, in all three modes — exactly matching
design doc §2's "Today: crashes" entry for C.

---

## Step 2: server-side `$size`/`$filter`/`$ifNull` rendering, hand-built pipeline

**Run:** the exact pipeline given in the brief, via `IMongoCollection<BsonDocument>.Aggregate<BsonDocument>(pipeline)`
against documents covering: 3-matching, 1-matching, 0-matching `Posts` arrays, `Posts: []`, `Posts` absent, and
`Posts: null`.

**Observed (with `$ifNull`):**

| Row | `Posts` state | `N` |
|---|---|---|
| three | 3 elements, `Rank` 1/2/3 (all `> 0`) | `3` |
| one | 3 elements, `Rank` 1/0/-1 (one `> 0`) | `1` |
| zero | 2 elements, `Rank` 0/-1 (none `> 0`) | `0` |
| empty | `[]` | `0` |
| missing | field absent | `0` |
| null | explicit BSON `null` | `0` |

Every row returned a number; no aborted command. The absent/null rows are `0`, exactly as expected.

**Run (re-run with `$ifNull` removed — `input: "$Posts"` instead of `input: {"$ifNull": ["$Posts", []]}`):**

- A row with a **present, well-formed array** (`present-only`, one element) still succeeds: `N = 1`.
- A row with **`Posts` missing** throws: `MongoDB.Driver.MongoCommandException`: *"Command aggregate failed:
  Executor error during aggregate command ... :: caused by :: The argument to $size must be an array, but was
  of type: null."*
- A row with **`Posts: null`** throws the identical exception, same message.

This confirms — as a measured fact, not an inherited claim — that `$ifNull` is **mandatory**: both the missing
and the explicit-null states abort the whole aggregate command with a hard server error, not merely a wrong
answer. (Aside: the *message* says "type: null" for the missing-field case too — MongoDB evidently evaluates a
missing `$Posts` reference to BSON `null` before handing it to `$filter`'s `input`, so both states hit the
identical code path inside the server; this is consistent with, and reinforces, why one `$ifNull: [..., []]`
guard covers both.)

---

## Step 3: `as` variable-name rules and nested scoping

**Run:** the same `$size`/`$filter` pipeline with `as` set to `"e"`, `"ee"`, `"_e"`, and `"E"` in turn, each
against a two-element `Posts` array (one matching, one not).

**Observed:**

| `as` name | Result |
|---|---|
| `"e"` | Accepted — `N = 1` |
| `"ee"` | Accepted — `N = 1` |
| `"_e"` | **Rejected**: `MongoCommandException`: *"Command aggregate failed: Invalid $project :: caused by :: '_e' starts with an invalid character for a user variable name."* |
| `"E"` | **Rejected**: identical message, *"'E' starts with an invalid character for a user variable name."* |

So the server requires a `$filter`/`$map` `as` name to **start with a lowercase ASCII letter** (or, per
MongoDB's documented rule not directly probed here, a non-ASCII character) — an uppercase or underscore-led
first character is rejected outright. This confirms the naming scheme the design (§4) says Task 1 will
implement — `"e"` at the outermost level, then `outer + "e"` per nesting level (`"ee"`, `"eee"`, …) — produces
only accepted names, since each successive name still starts with a lowercase `e`.

**Run:** a nested filtered count — an outer `$filter` over `Posts` (`as: "e"`) whose `cond` ANDs `$$e.Rank > 0`
with an inner `$size`/`$filter` over `$$e.Comments` (`as: "ee"`, `cond: {$gt: ["$$ee.Age", 0]}} > 0`, i.e. "this
post has rank > 0 AND at least one comment with age > 0" — against one post satisfying both and one post
satisfying neither.

**Observed:** `N = 1`, the expected count. The nested inner `$filter` correctly resolves `$$ee.Age` against the
comment element while `$$e.Rank`/`$$e.Comments` in the same `cond` correctly resolve against the outer post
element — distinct names at each level compose exactly as the design's naming scheme requires.

---

## Step 4: does `RenderNode`/`IsQueryDialectRenderable` decline cleanly or throw?

**Route taken:** per the brief owner's resolution of the ambiguity, the **read-then-confirm-by-unit-test**
route, using a hand-built `MongoExpression` tree with an existing node standing in for the not-yet-built
`MongoFilteredSizeExpression`.

**Choosing the stand-in mattered.** The first stand-in tried was `MongoElemMatchExpression` — any node kind
neither the size arm nor `IsQueryNativeComparison` matches would do for the *classifier* question, but
`MongoElemMatchExpression` is **also** unsupported by `MongoAggregationExpressionRenderer` (it has no case for
it and throws `NativeTranslationNotSupportedException: MongoAggregationExpressionRenderer does not support node
type 'MongoElemMatchExpression'`), so it produced a **different** outcome than the real future node will: a
throw from the `$expr` renderer itself. The design (§4) explicitly plans "one new arm in
`MongoAggregationExpressionRenderer`" for the real `MongoFilteredSizeExpression`, so the correct proxy is a node
that — like the future node — is unmatched by the *query*-dialect arms but **is** already supported by the
*aggregation*-expression renderer. `MongoElementRefExpression` fits exactly: it renders as `"$" + Path` in
`MongoAggregationExpressionRenderer.Render` but is not `MongoFieldExpression` (the comparison arm) or
`MongoSizeExpression` (the size-comparison arm), so it is "unmatched" for the query dialect in precisely the
way the future node will be.

**Run (unit tests, `MongoElementRefExpression` as the `Left` of a `MongoBinaryExpression(GreaterThan, ·, 2)`):**

1. `MongoQueryLanguageRenderer.IsQueryDialectRenderable(binary)` → **`false`**. A clean decline: the
   size-comparison arm's guard (`TryRenderSizeComparison(sizeComparison) is not null`) doesn't match because
   `binary.Left` isn't `MongoSizeExpression`; the catch-all `MongoBinaryExpression comparison =>
   IsQueryNativeComparison(comparison)` arm *does* match (it has no operator/left-type guard of its own) and
   `IsQueryNativeComparison` returns `false` because `binary.Left` isn't `MongoFieldExpression` either. No
   exception anywhere in this call.
2. The same binary wrapped in a `MongoElemMatchExpression` (mirroring §6's nested-in-quantifier row exactly —
   the outer `$elemMatch`'s `ElementPredicate` contains the not-yet-supported node) →
   `IsQueryDialectRenderable(outer)` is also **`false`** (the `MongoElemMatchExpression` arm recurses into
   `IsQueryDialectRenderable(elemMatch.ElementPredicate)`, i.e. the same binary from (1)).
3. `new MongoQueryLanguageRenderer().Render(binary, placeholders)` (calling `RenderNode` **directly, bypassing
   the classifier** — simulating what would happen if a future caller forgot the `IsQueryDialectRenderable`
   gate) — **does not throw.** `RenderNode`'s switch falls through the same two guarded arms (for the same
   reason as above), then through `MongoUnaryExpression`/`MongoFieldExpression`/`MongoInExpression`/
   `MongoRegexExpression`/`MongoElemMatchExpression` (none match — the node is a `MongoBinaryExpression`), and
   lands on the catch-all `_ => RenderAsExpr(node, placeholders)`. `RenderAsExpr` calls
   `MongoAggregationExpressionRenderer.Render` on the **whole binary**, which **succeeds** (both `Left`
   (`MongoElementRefExpression`) and `Right` (`MongoConstantExpression`) are supported there), producing a
   well-formed `{ "$expr": { "$gt": ["$Posts", 2] } }` document.
4. The same binary nested inside a real `MongoElemMatchExpression` and rendered directly via
   `Render(elemMatch, placeholders)` (simulating the exact §6 shape, classifier bypassed) — **also does not
   throw** — it produces `{"Posts": {"$elemMatch": {"$expr": {"$gt": ["$Posts", 2]}}}}`: syntactically valid
   BSON, C#-side, but the illegal-at-the-server shape the design's §6 explicitly worries about (`$expr` is a
   hard server error inside `$elemMatch`).

All four assertions passed (`Total: 4, Passed: 4, Failed: 0`).

**Answer to the brief's precise question:** `IsQueryDialectRenderable` returns `false` — a clean decline, no
throw. **`RenderNode` itself provides no independent safety net** — if the classifier's decline is ever bypassed
(e.g. a future bug that widens the size arm to also match the new node without checking `IsQueryDialectRenderable`
first), `RenderNode` will happily render a legal-C#/illegal-for-MongoDB `$expr`-inside-`$elemMatch` document
rather than throwing. This makes §6's nested-in-quantifier decline **entirely dependent** on the quantifier's
own admission path calling `IsQueryDialectRenderable` (as the existing `Any`/`All` quantifier machinery already
does for its own element predicate) *before* accepting an element predicate containing the new node — Task 1
must wire this the same way, not rely on `RenderNode` to catch a mistake.

---

## Step 5: explain plan for shape B's rendering

**Run:** seeded 200 documents (each with 3 `Posts` elements, `Rank` cycling through `0`–`4`), created an index
on `Posts.Rank` (`{"Posts.Rank": 1}`), then ran
`database.RunCommand<BsonDocument>({ explain: { aggregate: <coll>, pipeline: [{$match: {$expr: {$gt:
[{$size: {$filter: {input: {$ifNull: ["$Posts", []]}, as: "e", cond: {$gt: ["$$e.Rank", 0]}}}}, 2]}}}],
cursor: {} }, verbosity: "queryPlanner" })` — the explain-command-wrapping-aggregate route (the brief's first
option).

**Observed:** `winningPlan.stage = "COLLSCAN"`, `rejectedPlans: []` — the planner did not generate any IXSCAN
candidate for this predicate shape at all, exactly the same "not even attempted" result the sibling
`.Count`-in-a-predicate slice measured for its own array-index form (documented in `Query/AGENTS.md`'s
`.Count`-in-a-predicate note). This confirms the design's §4 "expectation, stated as an expectation rather than
a claim" — a `$size`-over-`$filter` inside `$expr` **is** a COLLSCAN, in both directions (no surprise index
usage, and no surprise total scan avoidance).

---

## Design deltas

No measurement in this spike contradicts design §3 (the sibling-node decision) or §6 (the declines). Specific
correspondences:

- **§2 scope table (shape dispositions):** A crashes in every mode (confirmed), B falls back gracefully with a
  working oracle across the *entire* ragged matrix (confirmed — stronger than the design assumed; see Step 1),
  C crashes identically to A (confirmed, newly measured — no pre-existing test covered it before this spike).
- **§4 (`$ifNull` mandatory, variable naming, index expectation):** all three confirmed exactly as stated (Steps
  2, 3, 5).
- **§6 (nested-in-quantifier decline, "falls out for free... must decline, and cleanly"):** confirmed exactly —
  `IsQueryDialectRenderable` returns `false` cleanly for the analogous shape, in both the bare and the
  nested-in-`$elemMatch` form. The one refinement worth flagging for Task 1 (not a contradiction, a
  precision-add): `RenderNode` has **no independent guard** of its own for this shape — the whole safety
  property rests on the quantifier's admission path calling `IsQueryDialectRenderable` before accepting an
  element predicate that contains the new node, exactly as the existing `Any`/`All` quantifier code already
  does. Task 1 should make sure this call site is exercised for the filtered-size case specifically (a targeted
  test, not just inference from the `Any`/`All` precedent).
- **§1 (root cause / top frame):** confirmed in substance; one precision correction — the crash's exact top
  frame is `MongoProjectionBindingExpressionVisitor.Visit` (a `ParameterExpression` case), one frame inside
  `.Translate`, not `.Translate` itself. Immaterial to the design's argument (translation-time, mode-independent
  crash), recorded for anyone chasing the exact line later.
- **§7 (driver-LINQ oracle availability):** confirmed no oracle for A or C (identical crash under explicit
  `DriverLinq`); confirmed a **full-matrix** working oracle for B — not merely "does not abort" but,
  per the fix-round-1 follow-up probe above, row-level agreement with the in-memory oracle at `k = 0` and
  `k = 1` including on the `missing`/`null` rows specifically. This is a bonus finding — Task 5's
  differential-oracle rows for shape B do not need to special-case ragged (missing/null) rows out of the
  `Native == DriverLinq` parity assertion the way the unfiltered wrapped-count slice's tests had to.

## GO / NO-GO

**GO.** Every measurement either confirms a design assumption exactly or refines it in the design's favor
(a full-matrix oracle for B, rather than a partial one). No measurement contradicts §3 or §6. The one
implementation note carried forward to Task 1: verify explicitly (with its own test) that the filtered-size
node's admission into a quantifier's element predicate calls `IsQueryDialectRenderable` before acceptance,
since `RenderNode` will not catch a mistake there.
