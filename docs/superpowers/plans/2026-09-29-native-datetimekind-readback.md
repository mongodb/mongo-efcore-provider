# Native DateTimeKind read-back fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the native query path from silently returning a different `DateTime` (wrong `Kind` and different
ticks) for properties configured with `HasDateTimeKind(Local)` (or `Utc`/`Unspecified` if those differ), wherever
native reads a value back through a generic CLR serializer instead of the property's own serializer.

**Architecture:** `BsonSerializerFactory.GetDateTimeSerializer` (`Serializers/BsonSerializerFactory.cs` ~:222) builds
a property-aware `DateTimeSerializer` that honours `IProperty.GetDateTimeKind()`. Several native read-backs don't use
it: GroupBy keys (read from `_id` through a generic CLR-type serializer), `$min`/`$max` accumulators, and terminal
scalar `Max`/`Min` (`MongoShapedQueryCompilingExpressionVisitor.DeserializeScalar`, which uses
`BsonTypeMapper.MapToDotNetValue`). The gate that is supposed to keep non-default-serialized properties off these
paths, `NativeGroupByBinder.HasDefaultKeySerialization` (~:185), checks the value converter and `BsonRepresentation`
but **not** `GetDateTimeKind()` — the one other property-dependent input to scalar read-back. Fix preference, in
order: (1) where the read-back knows its backing `IProperty`, read back through the property's serializer so the
shape stays native and correct; (2) otherwise decline via the default-serialization gate.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (`Debug EF8|EF9|EF10`), MongoDB C# driver, xUnit, Docker.

**Spec:** No spec doc. Evidence: step-2 Task 2 review (ledger
`.superpowers/sdd/2026-09-29-native-groupby-post-group-where-and-push-list/progress.md`, "Task 2: review"), probed
on EF10 with host TZ Europe/London: with `HasDateTimeKind(Local)`, NativeOnly returned `Kind=Utc` and different ticks
(June 2020 value: native `637266024000000000` = 10:00 Utc vs DriverLinq `637266060000000000` = 11:00 Local) for a
scalar GroupBy key, a composite key part, a nullable key, `g.Max/Min(x => x.OrderDate)`, `g.Max(x => x.NDate)`, and
terminal `q.Max(o => o.OrderDate)`. Plain projection and `Select(o => o.OrderDate).Distinct()` were correct
(`Local`). `HasDateTimeKind(Utc)` agreed everywhere. Owner decided (2026-09-29): fix as its own slice.

## Global Constraints

- Work directly on branch `native-EF-322-Native-LINQ-rebased` (no worktree). Commit messages start with `EF-322: `.
  **Do NOT push.**
- Preserve file BOMs. `src/` is nullable-enabled.
- Must compile and pass under `Debug EF8`, `Debug EF9`, `Debug EF10`.
- xUnit, plain `Assert.*`. Leave `MONGODB_URI`/`ATLAS_URI` unset. Never enable test parallelization.
- A recognizer must not mutate then decline.
- Every changed shape: NativeOnly result == hand-computed oracle == DriverLinq where DriverLinq is correct
  (entity materialization of the same property is the reference for what the value *should* be).
- DateTime tests must not depend on the machine's time zone passing by accident: use a value whose Local/Utc
  representations differ in **every** zone that has a non-zero offset, and assert `Kind` and ticks (or compare
  against the value materialized through a tracked entity query of the same row). Note in a comment that in a UTC
  host zone the tick check is weaker, and assert `Kind` regardless.
- Guards/fixes proven by mutation, results recorded.
- Stage only files you changed; never `nuget.config`.
- Scratch: `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/0b03de6a-225d-4390-bd26-17a062c748bb/scratchpad/dtk-task<N>/`.

## Review Focus

1. **Filters comparing a Local-kind property to a constant/parameter** (`Where(o => o.LocalDate > p)`) must not
   change behaviour — the constant is serialized through the property serializer today; confirm, and do not
   over-decline filters. Pinned by a Task 1 regression test.
2. **Every native read-back site that can surface a DateTime from a property**, not just the three GroupBy ones:
   computed projections (`o.LocalDate.AddDays(1)`), `$push` lists (step 2 declines — may shrink), join-scope inner
   leaves, owned-collection element projections, set-op/Distinct projections. Task 1 Step 1 enumerates and probes.
3. **Nullable `DateTime?`** with `HasDateTimeKind(Local)` behaves the same as non-nullable.
4. **`HasDateTimeKind(Utc)` and unconfigured (`Unspecified`)** keep working and stay native.
5. **EF8/EF9** — same behaviour.

---

### Task 1: Probe matrix, then fix read-back (property serializer where known; otherwise decline)

**Files:** determined by Step 1. Likely: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`
(`HasDefaultKeySerialization`, key/accumulator read-back), `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
(`DeserializeScalar` / scalar aggregate read-back), the GroupBy DOM/streaming shaper's `_id` read, and the `$push`
arm's local DateTimeKind check. Tests: a new `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeDateTimeKindReadbackTests.cs`.

- [ ] **Step 1: Probe matrix (before any code change).** Build a small model with `DateTime LocalDate`
  (`HasDateTimeKind(Local)`), `DateTime? NLocalDate` (Local), `DateTime UtcDate` (`HasDateTimeKind(Utc)`),
  `DateTime PlainDate` (unconfigured), plus a grouping key and an owned collection / navigation if cheap. For each
  shape below record NativeOnly vs DriverLinq vs tracked-entity reference (`Kind` + ticks) in the report:
  GroupBy scalar key; composite key part; nullable key; `g.Max/Min`; terminal `Max/Min`; `Select(o => o.LocalDate)`;
  `Select(o => new { o.LocalDate })`; computed `Select(o => o.LocalDate.AddDays(1))`; `Distinct()` over the date;
  a `Where(o => o.LocalDate > p)` filter (row set only); `$push` list (currently declines); join-scope inner leaf
  (`o.Owner.LocalDate`) if a navigation exists; `First()/Single()` reducer over a projected date; `Concat`/`Union`
  of projected dates. Mark each: correct / wrong / declines / driver-throws.
- [ ] **Step 2: Decide the fix per wrong site** — prefer property-serializer read-back (look up how the plain
  projection path gets it right; reuse `BsonSerializerFactory` — Query must ask the factory, never instantiate a
  serializer, per `Query/AGENTS.md` "vs Serializers"). Where the read-back has no backing `IProperty` (computed
  value), decide per C# semantics: `o.LocalDate.AddDays(1)` materialized by EF for a Local-kind property would be
  Local — if native can't reproduce it, decline. Record each decision in the report. If a fix would widen into the
  Serializers area substantially, report NEEDS_CONTEXT with the options instead.
- [ ] **Step 3: Write failing tests** — one per wrong site from Step 1 (NativeOnly == reference; `Kind` asserted),
  plus Review Focus 1 (filter unchanged), 3 (nullable), 4 (Utc/unconfigured still native via NativeAndParity).
- [ ] **Step 4: Implement**; **Step 5: tests green**; **Step 6: mutation proof** — revert each fix and confirm its
  test fails.
- [ ] **Step 7: If `HasDefaultKeySerialization` changed**, re-evaluate every caller (grep lists ~15 sites across
  NativeProjectionBinder, MongoExpressionTranslator.AllFieldsDefaultSerialized, NativeCardinalityBinder, QMTEV,
  NativeJoinScopeProjectionBinder) — each must still be correct, and filters (AllFieldsDefaultSerialized is used in
  predicates) must not over-decline Local-kind comparisons that are correct today. Shrink the step-2 `$push` arm's
  local DateTimeKind check if now redundant.
- [ ] **Step 8: Regression** — build all three; EF10 unit + full functional; spec classes NorthwindGroupBy,
  NorthwindAggregateOperators, NorthwindMiscellaneous, NorthwindSelect default mode. Record any spec failure with its
  first message line (don't fix spec tests here).
- [ ] **Step 9: Commit** — `EF-322: honour configured DateTimeKind in native read-back (GroupBy keys, min/max, scalar aggregates)`.

### Task 2: Spec suite, docs, full three-version verification

- [ ] **Step 1:** classify every spec failure (EF10 default + NativeOnly) by message: baseline mismatch / "no
  exception thrown" / other (stop and report).
- [ ] **Step 2:** flip/rebaseline as measured (scoped `EF_TEST_REWRITE_BASELINES=1` after data assertions pass;
  `git diff` every rewrite).
- [ ] **Step 3: Docs** — one terse bullet in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` "Durable invariants":
  scalar read-back must honour the property's configured `DateTimeKind` (serializer via `BsonSerializerFactory`, or
  decline); `HasDefaultKeySerialization`'s inputs are converter + `BsonRepresentation` + `DateTimeKind`.
- [ ] **Step 4: Full verification** — build all three; full unit + functional + spec on EF8/EF9/EF10 (0 failures);
  EF10 NativeOnly full-spec failure set by name vs `81cc0a6c` (detached worktree under scratch, removed afterwards):
  PASS→FAIL empty; name every FAIL→PASS.
- [ ] **Step 5: Commit** — `EF-322: re-baseline specs for DateTimeKind read-back; document invariant` (skip if
  nothing changed beyond docs — then commit the docs alone). Stage this plan file too (plan docs are tracked).
