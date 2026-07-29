# Spike findings — native owned-collection count projections (EF-322, Task 1)

Throwaway de-risking spike gating the "owned-collection count projections go native" slice. All spike
code was discarded; only this document was committed.

**Environment.** Branch `EF-322-owned-collection-count-projection` at `1b4c1d6`. EF10 (`Debug EF10`),
`MONGODB_URI`/`ATLAS_URI` both unset so TestContainers booted an isolated `mongodb/mongodb-atlas-local`
container. Scratch test class `SpikeCountProjectionTests`, fixture scaffolding copied verbatim from
`NativeOwnedCollectionCountTests.cs:39-219` (`Blog`/`Post`/`Comment`/`Home`/`Note`, `BlogModel`, `Row`,
`LenRow`, `PostDoc`, `Seed`, `SeedLengths`, `SeedWellFormed`, `CreateContext`, `UniqueCollectionName`),
plus `CreateContextWithLogging`/`SpyLoggerProvider` for MQL capture. Output captured via
`ITestOutputHelper` **and** appended to a file (the brief's `Console.WriteLine` is not reliably surfaced).

Commands (run twice — once on unmodified `src/`, once with the Task 3 production edit temporarily applied):

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test  tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpikeCountProjectionTests" --logger "console;verbosity=detailed"
```

---

## Summary of verdicts

| # | Claim the plan rests on | Verdict |
|---|---|---|
| Q1 | `Select(b => new { b.Title, N = b.Posts.Count })` throws `ArgumentException` in all three query modes today | **CONFIRMED** |
| Q2 | With the Task 3 fix applied, the bare count `Select(b => b.Posts.Count)` returns `0,0,0,1,2,3` under `Native`/`DriverLinq`, and `NativeOnly` declines cleanly | **MEASURED FALSE** (both halves of the value claim; the `NativeOnly` half is confirmed) |
| Q3 | There is a driver-LINQ oracle for the wrapped count, possibly limited to well-formed arrays | **MEASURED FALSE as stated** — there is **no** oracle at all today; one only exists *after* the Task 3 fix, and even then only on the well-formed seed |

Two of the three claims were false. Details, and the consequences for Tasks 2–5, below.

---

## Q1 — does the anonymous-wrapped count crash today? (unmodified `src/`)

Query: `db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count })`, seed = `SeedLengths`
(len0/len1/len2/len3/missing/null).

Verbatim:

```
Q1 Native: ArgumentException: Expression of type 'System.Collections.Generic.List`1[...+Post]' cannot be used for parameter of type 'System.Linq.IQueryable`1[...+Post]' of method 'Int32 Count[Post](System.Linq.IQueryable`1[...+Post])' (Parameter 'arg0')
Q1 DriverLinq: ArgumentException: ...same...
Q1 NativeOnly: ArgumentException: ...same...
```

**Verdict: CONFIRMED.** `ArgumentException`, identical message, in all three modes. This is a
translation-time crash (the projection-binding shaper fold runs before `MongoQueryMode` is read), so it is
mode-independent, exactly as the plan describes. Task 2 stays fallback-is-a-crash → native; it does **not**
need a `Native == DriverLinq` parity leg for the current behaviour, because there is no current behaviour
to be parity with.

---

## Q3 — is there a driver-LINQ oracle for the wrapped count?

Same query, `DriverLinq` only, on both seeds.

**Before the Task 3 fix** (unmodified `src/`) — verbatim:

```
Q3 full:       ArgumentException: Expression of type 'System.Collections.Generic.List`1[...+Post]' cannot be used for parameter of type 'System.Linq.IQueryable`1[...+Post]' ...
Q3 wellformed: ArgumentException: ...same...
```

**Verdict: MEASURED FALSE as the plan states it.** There is **no driver-LINQ oracle for the wrapped count
today, on any seed.** The `ArgumentException` fires in the projection binder before the driver's LINQ
provider is ever reached, so the "does the driver's own count rendering survive missing/null arrays?"
question the plan asks is *unanswerable in the current tree* — the driver never renders anything. Q3 as
written measures the same crash as Q1, not a driver behaviour.

**After the Task 3 fix** is temporarily applied — verbatim:

```
Q3 full:       ArgumentNullException: Value cannot be null. (Parameter 'source')
Q3 wellformed: OK -> len0=0,len1=1,len2=2,len3=3
```

So an oracle *comes into existence* with the Task 3 fix, and it is limited to the well-formed seed — but
**not for the reason the plan predicts.** The plan expected the driver to render `$size` server-side and
have MongoDB abort the aggregate on the missing/null rows (a `MongoCommandException`). It does not: see Q2.

---

## Q2 — bare count behaviour once unblocked (Task 3 fix temporarily applied)

Production edit temporarily applied: the `Queryable.Count`/`LongCount` case from the plan's Task 3, Step 3,
added to `MongoProjectionBindingExpressionVisitor.cs`'s `switch (method.Name)` after the
`nameof(Queryable.Select)` case.

Query: `db.Entities.AsNoTracking().Select(b => b.Posts.Count)`, seed = `SeedLengths`. Verbatim:

```
Q2 Native:     ArgumentNullException: Value cannot be null. (Parameter 'source')
Q2 DriverLinq: ArgumentNullException: Value cannot be null. (Parameter 'source')
Q2 NativeOnly: NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
```

Same query against `SeedWellFormed` (well-formed arrays only), with MQL capture:

```
Q2b Native:         OK -> 0,1,2,3
Q2b MQL Native:     Executed MQL query
                    EFTest-....Q2b_bare_count_mql_a7a171ee.aggregate([])
Q2b DriverLinq:     OK -> 0,1,2,3
Q2b MQL DriverLinq: Executed MQL query
                    EFTest-....Q2b_bare_count_mql_a7a171ee.aggregate([])
```

And the wrapped form on the well-formed seed also executes with `aggregate([])` under both `Native` and
`DriverLinq` (`Q1b MQL Native` / `Q1b MQL DriverLinq`, same empty pipeline).

**Verdict: MEASURED FALSE, in two distinct ways.**

### (a) The expected value `0,0,0,1,2,3` is wrong — it throws instead

The unblocked bare count does **not** return `0` for the `missing` and `null` rows. It throws
`ArgumentNullException: Value cannot be null. (Parameter 'source')` — i.e. `Enumerable.Count(null)`. The
`CollectionShaperExpression` materializes **`null`**, not an empty list, for a missing or explicitly-null
stored array in a *projection*.

This was isolated per row state (`Q4`), which also contrasts it against whole-entity materialization of the
identical documents. Verbatim:

```
Q4 empty-array whole-entity: Posts is count=0
Q4 empty-array bare-count:   OK -> 0
Q4 empty-array wrapped-count: OK -> len0=0
Q4 missing whole-entity:      Posts is count=0
Q4 missing bare-count:        ArgumentNullException: Value cannot be null. (Parameter 'source')
Q4 missing wrapped-count:     ArgumentNullException: Value cannot be null. (Parameter 'source')
Q4 explicit-null whole-entity: Posts is count=0
Q4 explicit-null bare-count:   ArgumentNullException: Value cannot be null. (Parameter 'source')
Q4 explicit-null wrapped-count: ArgumentNullException: Value cannot be null. (Parameter 'source')
```

Note the asymmetry, which is the sharp finding here: **whole-entity materialization of the very same
documents yields `Posts.Count == 0` for all three states**, and an empty stored array projects fine. Only
the *projection* path yields `null`, and only for missing / explicit-null. So the Task 3 fix as written
converts a translation-time `ArgumentException` (no data, every row) into a runtime
`ArgumentNullException` (no data, whenever any row has a missing/null array) — for the `SeedLengths`
matrix this is still "no data at all", just at a different phase. It is a real improvement only for
collections whose arrays are always present.

### (b) The count is computed **client-side**, not server-side

The emitted MQL is `aggregate([])` — an **empty pipeline**, in both `Native` and `DriverLinq`, for both the
bare and the anonymous-wrapped form. There is no `$size`, no `$project`, no server-side projection at all:
the whole document (including the entire `Posts` array) is fetched and the count is computed in-process.
This rules out both possibilities the plan's Task 3, Step 1 note offers ("renders the count server-side via
the driver's own `$size`" vs. not) — it is neither driver `$size` nor a `MongoCommandException`; the driver
LINQ provider is never asked to render the count, because the projection binder's rebuilt
`Enumerable.Count(shaper)` is a *client-side* fold over the already-materialized collection shaper.

Consequently the well-formed-seed oracle from Q3 is a **client-side** oracle. It is still a valid oracle
for *values*, but it proves nothing about server-side rendering.

### (c) The `NativeOnly` half of the claim is CONFIRMED

`NativeOnly` declines cleanly with `NativeTranslationNotSupportedException` ("Query projects a non-entity
result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback"), exactly as the plan expects — a
bare-scalar projection is `Route == Fallback`, and this is a translation-time decline, so it is
seed-independent (it fires on the full matrix seed too, before any row is read). The same clean
`NativeOnly` decline is observed for the *wrapped* form post-fix (`Q1 NativeOnly` after the fix).

---

## Consequences for the plan

### Task 2 (make the wrapped count go native)

- **No parity leg needed against current behaviour.** Q1 CONFIRMED: today the shape throws
  `ArgumentException` in all three modes, so there is no working `Native`/`DriverLinq` result to be
  parity with. Task 2 remains crash → native.
- **The differential oracle must be chosen deliberately.** Post-Task-3 there *is* a client-side
  `DriverLinq` oracle, but only on well-formed arrays (`SeedWellFormed`), and it is client-side so it
  cannot validate server-side `$size` semantics. Prefer the **in-memory LINQ oracle over the same
  `Expression`** (the pattern `Count_result_equals_the_in_memory_oracle_for_every_array_length_and_state`
  already establishes in `NativeOwnedCollectionCountTests`) as the primary gate; use `DriverLinq` on
  `SeedWellFormed` only as a secondary cross-check.
- **The native `$size` must be null-safe.** The native form must return `0` for missing / explicit-null
  arrays to match `LINQ`'s whole-entity semantics (`Posts.Count == 0`, per Q4's whole-entity rows) — i.e.
  the existing `MongoSizeExpression(nullSafe: true)` / `$ifNull` convention from the `.Count`-in-a-predicate
  slice, not a bare `$size`. Note this makes native *more* correct than the Task 3 fallback (which throws
  on those rows), so a `Native == DriverLinq` assertion over the full `SeedLengths` matrix will
  **fail by design** post-Task-2. Assert the in-memory oracle, not driver parity, on that seed.

### Task 3 (close EF-357 — bare count stops crashing)

- **The proposed test body will fail as written.** `Assert.Equal(new[] { 0, 0, 0, 1, 2, 3 }, counts)`
  against `SeedLengths` is MEASURED FALSE: the query throws `ArgumentNullException` before producing any
  values. Task 3 must either
  (i) run the value assertion against `SeedWellFormed` with expected `{0, 1, 2, 3}` — the split the plan's
      Step 1 note anticipates, but for the client-side-null reason above, **not** the driver-`$size`-aborts
      reason it gives — and separately pin the missing/null-row `ArgumentNullException` as a documented
      residual limitation; or
  (ii) additionally fix the `CollectionShaperExpression`-yields-`null` defect so the fold sees an empty
      list. That is a strictly larger change than the plan's Step 3 edit and touches a path every
      collection projection walks, so it should be an explicit decision, not a silent widening.
- **The Step 1 explanatory comment needs correcting on two points of fact:** the unblocked path does
  **not** render the count server-side (the pipeline is `aggregate([])`, whole document fetched, count
  folded client-side), and the `DriverLinq` leg's restriction to well-formed rows is *not* caused by the
  driver's `$size` aborting the aggregate.
- **Unchanged:** the `NativeOnly` leg (`Assert.Throws<NativeTranslationNotSupportedException>`) is correct
  as written — CONFIRMED.
- **Scope note:** EF-357 is only partly closed by the Step 3 edit. It fixes the *translation* crash; the
  shape still fails at runtime for any document with a missing or explicitly-null embedded array. The
  ticket resolution and the `Query/AGENTS.md` wording should say so rather than claiming a clean close.

### Task 4 (breadth, differential oracle, disjointness)

- `Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state` over `DifferentialRows()`
  is the right shape and is unaffected — provided Task 2's native path is null-safe. If `DifferentialRows()`
  includes missing/explicit-null `Posts` rows (as `SeedLengths` does), the **`Native` leg used to build the
  expected values must not be the projection path** — the plan's Step 1 code computes `expected` by
  materializing whole entities under `Native` and compiling the selector client-side, which is correct
  precisely because whole-entity materialization *does* give `Posts.Count == 0` (Q4). Keep it that way; do
  not "simplify" it into a projection query.
- `LongCount_projection_leaf_goes_native` expects `("missing", 0L), ("null", 0L)` — consistent with the
  null-safe `$size` requirement above, and a direct check that Task 2 got null-safety right.

### Task 5 (documentation)

- The `Query/AGENTS.md` note currently says the bare form "HARD-FAILS in every query mode
  (`ArgumentException`) … tracked as EF-357". After Task 3 that becomes: translation succeeds, values are
  correct for present arrays, and it throws `ArgumentNullException` at materialization for missing /
  explicit-null arrays, computed client-side over an empty pipeline. Document the residual limitation
  explicitly rather than marking EF-357 closed outright.
- Record the whole-entity-vs-projection asymmetry (Q4) — whole-entity materialization normalizes a
  missing/null embedded array to an empty list, the projection shaper does not. That is a general fact
  about the projection path, not specific to counts, and is likely to bite the next collection-projection
  slice.

---

## Discarded spike code

`tests/.../Query/SpikeCountProjectionTests.cs` was deleted and the temporary edit to
`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` was reverted
(`git checkout --`). `git status --short` confirms this document is the only change committed.
