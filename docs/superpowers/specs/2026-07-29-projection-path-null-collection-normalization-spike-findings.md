# EF-358 Task 1 — spike findings: measuring the projection-path null-collapse blast radius

**Date:** 2026-07-29. **Branch:** `EF-358`. **Throwaway artifact this doc reports on:**
`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef358SpikeTests.cs` (deleted at the end of this task —
see the brief's Step 6). **Environment:** `MONGODB_URI`/`ATLAS_URI` unset; TestContainers booted an isolated
`mongodb/mongodb-atlas-local` container per the repo's standard test setup. Build: `Debug EF10`.

Row order in every probe (per `OrderBy(b => b.Title)` over titles `"two"`, `"empty"`, `"missing"`, `"null"`,
alphabetical): **empty, missing, null, two** — matching the brief's stated expected order.

---

## 1. Verbatim probe output

### Step 2 — baseline run, unmodified `src/`

Command:

```
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Ef358SpikeTests" --logger "console;verbosity=detailed"
```

Verbatim console output (`Standard Output Messages`, unedited):

```
===== Native =====
array projection  Select(b => b.Posts) => 0, null, null, 2
array leaf        Select(b => new { b.Title, b.Posts }) => THREW ArgumentException:  Argument type 'System.Collections.Generic.IEnumerable`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' does not match the corresponding member type 'System.Collections.Generic.List`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' (Parameter 'arguments[1]')
bare count        Select(b => b.Posts.Count) => THREW ArgumentNullException: Value cannot be null. (Parameter 'source')
nested inner      Select(b => b.Posts) then inner Comments => , null, null, 1/1
CONTROL whole-entity => 0, 0, 0, 2
CONTROL Include(b => b.Posts) => 0, 0, 0, 2
===== DriverLinq =====
array projection  Select(b => b.Posts) => 0, null, null, 2
array leaf        Select(b => new { b.Title, b.Posts }) => THREW ArgumentException:  Argument type 'System.Collections.Generic.IEnumerable`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' does not match the corresponding member type 'System.Collections.Generic.List`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' (Parameter 'arguments[1]')
bare count        Select(b => b.Posts.Count) => THREW ArgumentNullException: Value cannot be null. (Parameter 'source')
nested inner      Select(b => b.Posts) then inner Comments => , null, null, 1/1
CONTROL whole-entity => 0, 0, 0, 2
CONTROL Include(b => b.Posts) => 0, 0, 0, 2
===== NativeOnly =====
array projection  Select(b => b.Posts) => THREW NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
array leaf        Select(b => new { b.Title, b.Posts }) => THREW ArgumentException:  Argument type 'System.Collections.Generic.IEnumerable`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' does not match the corresponding member type 'System.Collections.Generic.List`1[MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests+Post]' (Parameter 'arguments[1]')
bare count        Select(b => b.Posts.Count) => THREW NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
nested inner      Select(b => b.Posts) then inner Comments => THREW NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
CONTROL whole-entity => 0, 0, 0, 2
CONTROL Include(b => b.Posts) => 0, 0, 0, 2
```

Test run summary (verbatim):

```
Passed MongoDB.EntityFrameworkCore.FunctionalTests.Query.Ef358SpikeTests.Q1_which_shapes_reach_the_null_collapse [435 ms]
Test Run Successful.
Total tests: 1
     Passed: 1
 Total time: 10.6852 Seconds
```

### As a table — shape × mode × the four array states

`ArgumentException`/`ArgumentNullException`/`NativeTranslationNotSupportedException` throws are **not per-row**
— the `Probe` helper calls `.ToList()` once per shape/mode and the whole call either returns a joined string or
throws once. Where the throw happens independent of any row's data (translation-time, or a compile-time
expression-tree-construction error), that is noted; where it happens partway through eager enumeration (the
`bare count` case), that is noted too.

| Shape | Mode | empty | missing | null | two |
|---|---|---|---|---|---|
| `Select(b => b.Posts)` (array projection) | Native | `0` | `null` | `null` | `2` |
| `Select(b => b.Posts)` | DriverLinq | `0` | `null` | `null` | `2` |
| `Select(b => b.Posts)` | NativeOnly | THROWS `NativeTranslationNotSupportedException` ("non-entity result … forbids the driver-LINQ fallback") for the whole query, before any row is read | — | — | — |
| `Select(b => new { b.Title, b.Posts })` (array leaf) | Native | THROWS `ArgumentException` for the whole query, **before any row's array state matters** (see §2) | — | — | — |
| `Select(b => new { b.Title, b.Posts })` | DriverLinq | same `ArgumentException` | — | — | — |
| `Select(b => new { b.Title, b.Posts })` | NativeOnly | same `ArgumentException` (**not** `NativeTranslationNotSupportedException`) | — | — | — |
| `Select(b => b.Posts.Count)` (bare count) | Native | `0` (succeeds), then THROWS `ArgumentNullException` on the very next row (`missing`) — the whole `.ToList()` aborts, so no join string is ever produced | (abort) | (unreached) | (unreached) |
| `Select(b => b.Posts.Count)` | DriverLinq | same abort pattern | (abort) | (unreached) | (unreached) |
| `Select(b => b.Posts.Count)` | NativeOnly | THROWS `NativeTranslationNotSupportedException` for the whole query, before any row is read | — | — | — |
| nested inner (`Select(b => b.Posts)` then join inner `Comments`) | Native | `""` (empty join of zero posts) | `null` | `null` | `1/1` |
| nested inner | DriverLinq | `""` | `null` | `null` | `1/1` |
| nested inner | NativeOnly | THROWS `NativeTranslationNotSupportedException` for the whole query | — | — | — |
| CONTROL whole-entity | Native | `0` | `0` | `0` | `2` |
| CONTROL whole-entity | DriverLinq | `0` | `0` | `0` | `2` |
| CONTROL whole-entity | NativeOnly | `0` | `0` | `0` | `2` |
| CONTROL `Include(b => b.Posts)` | Native | `0` | `0` | `0` | `2` |
| CONTROL `Include(b => b.Posts)` | DriverLinq | `0` | `0` | `0` | `2` |
| CONTROL `Include(b => b.Posts)` | NativeOnly | `0` | `0` | `0` | `2` |

---

## 2. The flip surface

Shapes/modes whose **results** change once the full fix (Edit 1 + Edit 2 from the design) lands in Task 2:

| Shape | Modes in the flip surface | What changes |
|---|---|---|
| `Select(b => b.Posts)` (array projection) | **Native, DriverLinq** | `missing`/`null` rows: `null` → `0` (empty collection, `Count == 0`). `empty`/`two` unaffected (already `0`/`2`). |
| `Select(b => b.Posts.Count)` (bare count) | **Native, DriverLinq** | `missing`/`null` rows: `ArgumentNullException` (aborts the whole query) → `0`. This is EF-357's residual (§3.5) — closes the ticket fully. |
| nested inner (`Select(b => b.Posts)` then inner `Comments`) | **Native, DriverLinq** | `missing`/`null` outer rows: `null` → `""` (empty join). **Correction: this is a re-measurement of the same outer-array flip as `Select(b => b.Posts)` above, not a distinct data point** — see the note immediately below the table and §4c. |

**Correction on the "nested inner" row, made on review.** This row does **not** exercise the design's §4.1
"nested collection-in-collection, ragged **inner** array" scenario — the `CreateGetBsonArray` parent-document
path for a ragged array nested *inside* an already-materialized owned collection. Two reasons, both visible in
the probe code that ran:

- The seed's `PostsOf` helper gives every `Post` exactly one well-formed `Comment` — there is no missing or
  explicitly-null `Comments` anywhere in the seeded data, so no ragged inner array was ever round-tripped.
- The probe's query is the identical LINQ query as the "array projection" row —
  `.OrderBy(b => b.Title).Select(b => b.Posts)` — just post-processed client-side with an extra `.Select` over
  the already-materialized `List<Post>`. It measures the same server round-trip and the same **outer**-array
  null-collapse a second time, not a new server-side path.

So this row's flip (`null` → `""` for the `missing`/`null` outer rows) is redundant evidence for the
already-confirmed outer-array flip (same rows, same mechanism as `Select(b => b.Posts)`), not independent
confirmation of the design's predicted ragged-**inner**-array flip. **The ragged-inner-array scenario itself
was not measured by this spike at all** — see §4c.

**Not in the flip surface**, and why:

- **`Select(b => new { b.Title, b.Posts })` (array leaf) — excluded entirely.** It throws the identical
  `ArgumentException` in every mode for every row, including the well-formed `two` row, so the throw is
  independent of array state — the null-collapse conditional is never reached for this shape at all (see §5 for
  why: a pre-existing, unrelated anonymous-type-constructor/shaper type mismatch). Applying the fix changes
  nothing here. This **narrows** the design's §4.1-predicted flip surface by one row; it does not widen it.
- **All three shapes under `NativeOnly`** — each throws `NativeTranslationNotSupportedException` (array
  projection / bare count / nested inner: a bare-array or bare-scalar projection body never populates a native
  `Projection`, so it's not natively representable and `NativeOnly` forbids the fallback) or the same
  `ArgumentException` (array leaf). This is unrelated to the null-collapse and unaffected by the fix — these
  shapes never reach native routing regardless of array state, before or after Task 2.
- **CONTROL whole-entity, CONTROL `Include`** — unaffected in all three modes, both before and predicted after
  (see §4/§5 below — this is the GO/NO-GO gate).

## 3. Q1 answer — does `DriverLinq`'s array projection reach this site?

**Yes.** Every non-control, non-`NativeOnly` row is byte-for-byte identical between `Native` and `DriverLinq` —
same values for the array projection, same `ArgumentException` for the array leaf, same abort for the bare
count, same nested-inner values. This confirms design §1.2's claim directly: `BsonDocumentInjectingExpressionVisitor`
is applied unconditionally to the shaper body before the native-vs-fallback split has any bearing, so the driver
does **not** render its own projection for this shape — it goes through the identical shared shaper machinery
as `Native`. **`DriverLinq` is fully in the flip surface, on the same terms as `Native`.**

## 4. Q2 answer — does `FindCollectionShaper` survive Edit 1 alone?

**Yes — green.** Edit 1 (deleting the `Expression.Condition` null-collapse in
`BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case, leaving the `Expression.Assign`
byte-identical) was applied temporarily, `src/` rebuilt, and the streaming coverage run:

```
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeMaterializerOnePassTests|FullyQualifiedName~NativeOwnedCollectionWholeEntity|FullyQualifiedName~StreamingEligibilityTests"
```

Verbatim result:

```
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 764 ms - MongoDB.EntityFrameworkCore.FunctionalTests.dll (net10.0)
```

(13 = 7 in `NativeMaterializerOnePassTests.cs` + 6 in `NativeOwnedCollectionWholeEntityTests.cs`, both matched
inside the `FunctionalTests` project the command targets; `StreamingEligibilityTests` lives in the
`UnitTests` project and contributed 0 matches to this run — this is the brief's command run verbatim, not a
narrowed substitute.)

This confirms design §3.3's prediction: `FindCollectionShaper`'s `BlockExpression` walk (backwards) finds the
collection shaper at the block's last position once the `ConditionalExpression` wrapper is gone, with no code
change needed to that arm. The temporary edit was reverted immediately after
(`git checkout -- src/MongoDB.EntityFrameworkCore/Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs`;
confirmed by `git diff --stat src/` returning empty and a full `Debug EF10` rebuild succeeding on the reverted
tree).

## 4b. Explicitly deferred — projected reference collection control

The design's §4.1 table also lists a projected **reference** collection (`Select(c => c.Orders)`, the second
`CollectionShaperExpression` construction site, `$lookup`-backed) as a control predicted unaffected. This
probe's fixture is a single owned-collection entity (`Blog { List<Post> Posts }`) and has no reference
navigation, so that control was **not measured here**. It is deferred to Task 2's `CrossCollectionIncludeTests`
/ `QueryModeGateIncludeTests` run, per the brief's Step 5 item 4b. This is a deliberate scope gap in this spike,
not a silently-dropped check.

## 4c. Explicitly deferred — ragged INNER array (collection-in-collection)

The design's §4.1 table lists "nested collection-in-collection, ragged **inner** array" — a `Post` whose own
`Comments` is missing or explicitly `null`, reached via the `CreateGetBsonArray` parent-document path — as a
row expected to show the flip. **This spike did not measure it.** As detailed in §2's correction: the seed
(`PostsOf`) gives every `Post` exactly one well-formed `Comment`, so no ragged inner array was ever stored or
read back, and the "nested inner" probe row's query is the same `Select(b => b.Posts)` as the array-projection
row, merely post-processed client-side — it re-observes the outer-array collapse, not an inner one. This
scenario is deferred to Task 3 (whose `Nested_collection_in_collection_normalizes_a_ragged_inner_array` test,
per the plan, is the first thing that actually exercises it) rather than silently treated as covered here.

## 5. Why the array-leaf shape is excluded, in detail

The `ArgumentException` message — `"Argument type 'IEnumerable<Post>' does not match the corresponding member
type 'List<Post>' (Parameter 'arguments[1]')"` — is the validation error `Expression.New(ConstructorInfo,
IEnumerable<Expression>, MemberInfo[])` raises when an anonymous-type constructor argument's static type doesn't
match the corresponding member's declared type. This fires while the shaper expression tree is being
**constructed** (translation/shaper-compilation time), not while any document is being read — which is exactly
why it throws identically for the `two` row (a well-formed, non-null, non-empty array) as it does for
`missing`/`null`/`empty`: the failure has nothing to do with the stored array's state. It is a pre-existing,
structural mismatch, unrelated to EF-358's null-collapse, and orthogonal to the fix under design in this branch.
The implementation plan already flagged this exact possibility ahead of running the spike — see
`docs/superpowers/plans/2026-07-29-projection-path-null-collection-normalization.md`, Task 2, the paragraph
beginning "If that same test fails because `Select(b => new { b.Title, b.Posts })` …" — so this is a confirmation
of a named risk, not a new discovery, and it has been folded back into the design doc (§4.1, correction note)
per the brief's Step 5 instruction not to carry a refuted claim forward.

## 6. GO / NO-GO

**GO.**

Reasoning, against the brief's Step 5 criteria exactly:

1. **Did a CONTROL move under Edit 1 alone?** No. The whole-entity/`Include` streaming coverage
   (`NativeMaterializerOnePassTests` + `NativeOwnedCollectionWholeEntityTests`, 13 tests) stayed green under
   Edit 1 alone (§4), and the Q1 probe's own CONTROL rows (`CONTROL whole-entity`, `CONTROL Include`) are
   unaffected on unmodified `src/` in all three modes (§1) — consistent with the design's claim that neither
   control ever reaches the site in the first place (whole-entity fills collections via
   `IClrCollectionAccessor`; `Include` is `$lookup`-backed, which always writes an array).
2. **Is the flip surface materially wider than predicted?** No. The design's §4.1 table predicts **four**
   non-control rows would show the flip — array projection, array leaf, bare count, and the nested
   collection-in-collection ragged-**inner**-array case. (The projected reference collection is labeled a
   **control** — "predicted unaffected" — in that same table, the same category as `Include` and whole-entity;
   it is not a flip prediction, and counting it as one in an earlier draft of this reasoning was a miscount,
   corrected here.) Measured against those four: array projection and bare count confirmed the predicted flip,
   in **both** `Native` and `DriverLinq`; array leaf is excluded entirely, by an unrelated, pre-existing
   hard-fail flagged as a risk in the implementation plan before this spike ran (§5) — narrower than predicted,
   not wider; the ragged-inner-array case was **not actually exercised** by this spike's "nested inner" probe
   row (§2, §4c) and remains unmeasured, deferred to Task 3. So: two rows confirmed exactly as predicted, one
   row narrower than predicted, one row still open — and in no case did a shape outside the design's predicted
   four turn up in the flip surface. Nothing here is wider than predicted.

No open concerns block Task 2. The one correction made in the same task (per the brief's instruction) is
recorded in `docs/superpowers/specs/2026-07-29-projection-path-null-collection-normalization-design.md` §4.1:
the array-leaf table row is annotated to note it does not exercise the null-collapse and is excluded from the
flip surface.
