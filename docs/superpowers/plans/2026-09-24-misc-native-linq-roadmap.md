# Miscellaneous Native-LINQ Coverage Roadmap (non-GroupBy, non-Include, non-Join)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement Track B task-by-task. Track A phases are each a future
> spec+plan cycle of their own (see "How to pick up a Track A phase" below) — do not attempt to execute them
> directly from this document.

**Goal:** Reduce reliance on the MongoDB C# Driver's LINQ pipeline by growing native (`MongoExpressionTranslator`)
coverage of *miscellaneous scalar function and operator* translation — explicitly **excluding** anything that
touches `GroupBy`, `Include`, or the shared join/multi-collection-navigation-subquery machinery, since those are
being actively worked by other agents on other branches (this branch is `EF-322-Native-LINQ-rebased`).

**Spec:** No separate design doc — this is a scoping/roadmap document; each Track A phase below gets its own
`docs/superpowers/specs/...` + `docs/superpowers/plans/...` pair when it is actually picked up, per this repo's
existing cadence (e.g. `2026-08-26-native-join-translation-design.md`, `2026-09-23-native-typeis-non-hierarchy.md`).

## Background — how this scope was determined

The obvious starting point, `NorthwindMiscellaneousQueryMongoTest.cs`, has 158 `AssertTranslationFailed` /
`AssertNoMultiCollectionQuerySupport` overrides. Categorizing them by the informal `EF-XXX` gap codes used in
that file's comments:

| Bucket | Count | Nature |
|---|---|---|
| `EF-216` "Cross-document navigation access" | 121 | `Any`/`All`/`Exists`/`Where` composition over navigation collections, correlated subqueries, joins |
| `EF-X001` "Subquery selection" | 35 | `DefaultIfEmpty`, `SelectMany` subqueries, `Skip`/`Take` over a subquery |
| `EF-X002` | 21 | Sub-case of the above, wrapped in `AssertNoMultiCollectionQuerySupport` |
| `EF-X017`/`EF-X022`/`EF-220` | 4 | Join/GroupJoin shape variants |
| `EF-149` | 1 | Explicitly GroupBy |
| Two `// Failed:` transitional markers | 2 | Per `tests/.../SpecificationTests/AGENTS.md`, that marker is reserved for Include-rebaselining work |
| Genuinely isolated | 2 | `Select_expression_datetime_add_ticks` (`EF-X003`), `Where_nanosecond_and_microsecond_component` (`EF-X015`) |

**~95% of that file's remaining fallbacks trace to one root cause** — the native translator's lack of general
multi-collection/subquery (`$lookup`-based) translation for navigation access — which is exactly the machinery
the Join/Include work in flight is extending. Per direction from the branch owner, this roadmap avoids that
machinery entirely and instead widens the search to other spec test files and, critically, to **coverage gaps
that don't show up as `AssertTranslationFailed` at all** because driver-LINQ already produces correct results
for them silently.

### The bigger finding: the EF10 `Translations` test-base family is wholly unimplemented

Under EF10, upstream EF Core restructured its scalar function/operator conformance suite into
`Microsoft.EntityFrameworkCore.Query.Translations.*`: `MathTranslationsTestBase`, `StringTranslationsTestBase`,
`MiscellaneousTranslationsTestBase`, `DateOnlyTranslationsTestBase`, `DateTimeTranslationsTestBase`,
`DateTimeOffsetTranslationsTestBase`, `TimeOnlyTranslationsTestBase`, `TimeSpanTranslationsTestBase`,
`ByteArrayTranslationsTestBase`, `EnumTranslationsTestBase`, `GuidTranslationsTestBase`, and four
`Operators.*` bases (Arithmetic/Bitwise/Comparison/Logical/Miscellaneous).

**None of these are implemented for EF10.** They are explicitly listed in
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs:189-204`
(`IgnoredTestBases`, inside the `#if !EF8 && !EF9` block), with the adjacent comment (line 159-160):
`// NorthwindFunctionsQueryMongoTest exists only for EF8 and EF9 — the EF10 build deliberately omits it.`

The EF8/EF9-only predecessor,
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindFunctionsQueryMongoTest.cs`
(guarded by `#if EF8 || EF9`), covers similar ground for the older versions and confirms scalar-function
translation (`Math.*`, `MathF.*`, `Convert.*`, string trim/comparison overloads) has **no native support
anywhere in `src/` today** — a repo-wide grep of the native translator
(`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` and every
other file under that directory) turns up zero handling of `Math.Abs`/`Ceiling`/`Sqrt`/etc. Every one of those
EF8/EF9 tests that currently passes (all but `MathF.*`, `Math.Min`/`Max`, `Double.DegreesToRadians`/
`RadiansToDegrees`, which fail even via the driver) is passing **silently through driver-LINQ fallback** —
invisible to a comment-grep, and currently not exercised under EF10 at all.

This is the primary opportunity: a large, cleanly-scoped, entirely non-overlapping (no `$lookup`, no grouping,
no eager-loading) body of work.

## Track B — quick wins in existing Northwind spec files (do these first)

Six tests, two categories, all confirmed isolated (re-verified against current file state — two tickets from
the initial scan, `EF-228` float-Sum/Average truncation and `Contains_over_keyless_entity_throws`/`EF-202`,
turned out on inspection to already be resolved or to be a permanently-correct-everywhere exception check, not
a driver-LINQ gap — dropped from this list).

### B1: `EF-222` — `EF.Functions.Like` — **DONE for constant patterns, 2026-09-24**

Turned out bigger than a "non-string column" fix: `EF.Functions.Like` had **zero** native translation for
*any* shape, and — unlike every other gap in this doc — no driver-LINQ fallback either (the C# driver's own
LINQ v3 provider throws `ExpressionNotSupportedException` for it too; confirmed by `MongoSpecTestHelpers
.AssertNativeTranslationFailedAsync`'s accepted-exception list, which every `Like` test in
`NorthwindDbFunctionsQueryMongoTest.cs` was wrapped in). So there was no existing behavior to preserve parity
with — this is new capability, and its case-sensitivity was this provider's own free choice (case-insensitive,
matching the upstream conformance suite's own expected-result shape, confirmed empirically against real data).

Implemented (commit `EF-322: native EF.Functions.Like translation for constant patterns`):
- `MongoRegexKind.Like` (`Expressions/MongoRegexExpression.cs`) + wildcard-to-regex conversion in
  `MongoRegexPatternBuilder.BuildLikePattern` (`%`→`.*`, `_`→`.`, else `Regex.Escape`, anchored `^...$`).
- `MongoExpressionTranslator.Like.cs` (new): recognizes `DbFunctionsExtensions.Like` with a **compile-time
  constant pattern** (the only shape a wildcard conversion can happen for). Two sub-cases: match expression is
  also constant → collapses to a compile-time `true`/`false` via `Regex.IsMatch` (this one is *mandatory*, not
  optional — `DbFunctionsExtensions.Like`'s C# body unconditionally throws, so there is no client-evaluation
  fallback for an all-literal call); match expression is a plain `string` field → `MongoRegexExpression{Kind:
  Like}`, rendered as `$regularExpression` with options `"is"`.
- `MongoAggregationExpressionRenderer.CanRender`'s regex arm now explicitly excludes `Kind: Like` (no `$expr`
  rendering exists for it — a LIKE pattern needs wildcard conversion, not a literal substring search), keeping
  the `CanRender`⇔`Render` exhaustiveness invariant honest per that file's own comment.
- Fixed, verified native under `MONGODB_EF_NATIVE_ONLY=1` on all three EF versions, full Query suite green
  (EF8/EF9/EF10, spec + functional + unit): `Like_literal`, `Like_all_literals`
  (`NorthwindDbFunctionsQueryMongoTest.cs`), `Where_Like_and_comparison`, `Where_Like_or_comparison`
  (`NorthwindWhereQueryMongoTest.cs`, EF8/EF9-only file).
- The `EF_TEST_REWRITE_BASELINES=1` auto-rewriter **silently no-ops** for a call site inside an
  `#if EF8 || EF9` region — it parses the file with `CSharpSyntaxTree.ParseText` and no preprocessor symbols,
  so that region is inactive/skipped trivia to Roslyn regardless of which config the test actually ran under.
  Left a `.tmp` file both times it tried. Worked around by reading the always-written `QueryBaseline.txt` (in
  the test project's `bin/<config>/net10.0/` output directory) for the captured MQL and hand-applying it. Flag
  this as a real tool limitation for any future baseline regeneration inside a version-`#if` block — check
  `QueryBaseline.txt` when the in-place rewrite silently does nothing.

**Not done — still declines (unchanged from before, no regression):**
- `Like_identity` (pattern is itself a field, e.g. `EF.Functions.Like(c.X, c.X)`) — a per-document pattern
  can't be wildcard-converted at translation time; doing it at Build/per-execution time (like a parameterized
  `StartsWith` term already does) would need escape-awareness threaded through `PlaceholderTable`/
  `MongoPipelineFactory` too. Deferred.
- The 3-argument escape-character overload (`Like_literal_with_escape`, `Like_all_literals_with_escape`).
- `Like_with_non_string_column_using_ToString`/`_using_double_cast`
  (`NorthwindWhereQueryMongoTest.cs`, EF8/EF9-only) — a `ToString()`/cast-wrapped non-string receiver. The
  match-expression resolver requires a bare `string`-typed field; extending it to unwrap a `ToString()` call or
  numeric-to-`string` cast (composing with the receiver's own already-native to-string translation — confirmed
  via `Select_expression_int_to_string`/`_long_to_string`, native per
  `NorthwindMiscellaneousQueryMongoTest.cs:2660-2673`) is a natural next slice, not attempted this round.

### B2: `EF-X015` + `EF-X003` — **CLOSED, not fixable, 2026-09-24**

Both tests decline today, and correctly so: BSON's `Date` type is a fixed millisecond-since-epoch signed
`int64` (BSON spec, type `0x09`) — there is no sub-millisecond representation at the storage/wire layer at
all, for either a stored value or one computed server-side. This is not a translation gap a smarter native
translator could close; it's a hard ceiling of the wire format itself.

- `Where_nanosecond_and_microsecond_component` — `DateTime.Nanosecond`/`Microsecond` component access can
  never be answered from a BSON `Date`, since that data was never retained in the first place. Permanent
  decline, matching root `AGENTS.md`: "the exception type thrown for an *unsupported* feature... is not a
  break." No further action.
- `Select_expression_datetime_add_ticks` — even if a native translator computed sub-millisecond precision via
  numeric arithmetic (convert to epoch millis, add `ticks / 10000.0`, convert back), the RESULT still has to
  round-trip through a BSON `Date` to reach the client, which truncates it right back to millisecond
  resolution — reproducing the exact silent-precision-loss bug the driver-LINQ fallback already has today
  (per the existing comment: "driver-LINQ mode... executes and returns wrong data"). Native declining outright
  (current behavior) is strictly better than driver-LINQ's silent wrong-data path, so there is nothing to
  improve here either — the decline itself is the correct, final behavior.

No code change. Dropped from further consideration.

## Track A — stand up the EF10 `Translations` test-base family

This is the sustained, larger track. Each phase below is its own future spec+plan cycle; this section only
records **priority order and rationale**, not implementation detail (which needs its own investigation into the
exact upstream test bodies — not available as source locally, only as a compiled
`Microsoft.EntityFrameworkCore.Specification.Tests` NuGet package, so each phase's spec must start by exercising
the base class reflectively or via a throwaway test run to enumerate its actual test methods).

**Per-phase pattern** (established by every prior `EF-322` phase): create the Mongo test class inheriting the
upstream `*TranslationsTestBase<TFixture>`, override each test method following the "override, call `base`, then
`AssertMql`, or `AssertTranslationFailed` + `AssertMql` for genuine gaps" convention, add whatever native
translator support the failures reveal, remove the test base from `MongoComplianceTest.IgnoredTestBases`
(`MongoComplianceTest.cs:189-204`) once its class exists, and add the new file to the
[test-area mirror table](../../../tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md) mentally (no
table edit needed — it's `Query/`, already listed).

1. **`MathTranslationsTestBase`** — `Math.*`/`MathF.*` (Abs, Ceiling, Floor, Round, Truncate, Sqrt, Pow, Sign,
   Min, Max, Exp, Log, Log10, trig functions, Degrees/Radians). Highest priority: purely mechanical
   method-name-to-MQL-operator mapping (`$abs`, `$ceil`, `$floor`, `$round`, `$trunc`, `$sqrt`, `$pow`, ...),
   no navigation, no aggregation-over-collection ambiguity, and the EF8/EF9 file's tickets (`EF-237` MathF,
   `EF-238` Min/Max, `EF-239` Sign, `EF-240` Degrees/Radians) already tell us exactly which cases currently fail
   even via the driver, so those are guaranteed-fixable gaps, not just fallback-to-native conversions.
2. **`StringTranslationsTestBase`** — Trim variants, `StartsWith`/`Contains`/`EndsWith` with `StringComparison`
   overloads (EF8/9 ticket `EF-243`), `String.FirstOrDefault`/`LastOrDefault` (`EF-248`). **Exclude** any
   `String.Join`-as-aggregate-over-a-navigation-collection shape if the upstream test turns out to need one
   (`EF-245` in the EF8/9 file — verify per-test whether it's a scalar `string.Join(sep, array-of-columns)` or an
   aggregate over `c.Orders.Select(...)`; only the former is in scope here).
3. **`MiscellaneousTranslationsTestBase`** — `Convert.To*` family (`EF-235`: ToBoolean/ToByte/ToDecimal/
   ToDouble/ToInt16/ToInt32/ToInt64/ToString).
4. **Temporal bases** (`DateOnlyTranslationsTestBase`, `DateTimeTranslationsTestBase`,
   `DateTimeOffsetTranslationsTestBase`, `TimeOnlyTranslationsTestBase`, `TimeSpanTranslationsTestBase`) — builds
   directly on the existing native `DateTime.AddXxx` work (`EF-322`, see Track B2's reference) and likely
   subsumes `DateOnly.FromDateTime` (`EF-242`), `DateTime` subtraction (`EF-246`), `DateTimeKind` handling
   (`EF-X006`) as natural side effects of full coverage.
5. **`Operators.*`** (`ArithmeticOperatorTranslationsTestBase`, `BitwiseOperatorTranslationsTestBase`,
   `ComparisonOperatorTranslationsTestBase`, `LogicalOperatorTranslationsTestBase`,
   `MiscellaneousOperatorTranslationsTestBase`) — operator-level translation is likely mostly already correct
   (binary `+`/`-`/`&&`/`||`/comparisons are core to any predicate translator); expect this phase to be mostly a
   coverage/gap-filling pass rather than new-capability work. One known permanent gap to expect and document
   rather than fight: **bitwise XOR** (`EF-X013`, `Where_bitwise_xor` in `NorthwindWhereQueryMongoTest.cs`) — MongoDB's
   aggregation pipeline has no native bitwise-XOR operator, so this is a capability ceiling, not a driver-vs-native
   question; confirm and document as an intentional decline rather than spending time on it.
6. **`ByteArrayTranslationsTestBase`, `EnumTranslationsTestBase`, `GuidTranslationsTestBase`** — smallest,
   lowest-priority, do last; likely mostly-passing already since Guid/enum/byte-array equality and basic member
   access are common paths already exercised elsewhere.

### How to pick up a Track A phase

For phase *N*: write a `docs/superpowers/specs/YYYY-MM-DD-native-translations-<name>-design.md` following this
repo's brainstorming→spec→plan cadence. Its investigation step must first **run the upstream test base against
a throwaway/minimal Mongo test class** (even one that just calls `base.SomeTest()` with no `AssertMql`, to see
what upstream actually asserts and what currently throws) rather than guessing test bodies from the compiled
DLL's method-name strings — this repo's own convention (`tests/.../AGENTS.md`) is empirical baseline capture via
`EF_TEST_REWRITE_BASELINES=1`, not hand-authored expectations.

## Explicitly out of scope (for this whole roadmap)

- `GroupBy` (`EF-149` and friends) and `Include`/`ThenInclude` (the two `// Failed:` transitional markers, plus
  everything under `NorthwindIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`,
  `NorthwindStringIncludeQueryMongoTest.cs`, `NorthwindEFPropertyIncludeQueryMongoTest.cs`) — other agents' active work.
- The shared join/multi-collection-navigation-subquery machinery (`EF-216`, `EF-X001`, `EF-X002`, `EF-X017`,
  `EF-X022`, `EF-220`, and all of `NorthwindJoinQueryMongoTest.cs`/`NorthwindNavigationsQueryMongoTest.cs`) — same
  underlying `$lookup` infrastructure the Include work extends; avoided per explicit branch-owner direction even
  though the individual LINQ operators involved (`Any`/`All`/`Exists`/`SelectMany`/`Join`) are not literally
  `GroupBy`/`Include`.
- `Where_bitwise_xor` (`EF-X013`) — permanent MongoDB capability ceiling, not a native-vs-driver gap.

## Self-Review

- **Placeholder scan:** No TBD/TODO. Track A phases are intentionally left at roadmap granularity (not
  step-by-step) because their exact scope depends on upstream test bodies not available as local source — this
  is stated explicitly as the first step of picking up any phase, not left ambiguous.
- **Internal consistency:** Track B's exclusion of `EF-228` (float Sum/Average) and `EF-202`
  (`Contains_over_keyless_entity_throws`) is explained (already-fixed / not a real driver-LINQ gap) rather than
  silently dropped, matching how the initial scan's `EF-X013` bitwise-xor exclusion is explained in the "Explicitly
  out of scope" section.
- **Scope check:** Track B is small enough to execute directly; Track A is explicitly NOT — each phase requires
  its own spec+plan per this repo's established cadence, and this document says so rather than pretending a
  6-phase roadmap can be one flat task list.
- **Status (2026-09-24):** Track B is closed. B1 landed native support for `EF.Functions.Like`'s two
  constant-pattern shapes (4 tests fixed, real new capability, not just a native conversion of existing
  fallback behavior) and left three narrower gaps as documented, non-regressing declines. B2 turned out to
  need no code at all — both its tests were already correctly declining a permanent BSON `Date`
  millisecond-resolution ceiling, not a translation gap. Track A (`MathTranslationsTestBase` first) is next.
