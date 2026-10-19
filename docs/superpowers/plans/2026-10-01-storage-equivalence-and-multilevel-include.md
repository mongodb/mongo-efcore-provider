# Unified stored-serialization equivalence + native multi-level collection-Include-over-join

Branch `EF-322c` (commit directly; no worktrees; don't push). Base: 748111b1. Owner rulings (2026-10-01):
(1) adopt "option C" — one shared stored-serialization equivalence rule for anonymous-key joins and projected set
ops; (2) make collection-Include-over-a-multi-level-join queries run natively.

These ARE intended behaviour changes (more shapes admitted natively), so each is test-driven: write the failing
tests first (record RED/GREEN), then implement.

## Global Constraints

- Read `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` first; its invariants bind (silent-wrong-data rules:
  gate calls the fix's predicate; recognizer must not mutate then decline; new native join shapes tested under
  explicit DriverLinq too; native results compared to an oracle).
- Correctness over coverage: anything you can't prove correct must decline (fall back), never emit a pipeline that
  might return wrong rows. When admitting a shape natively, test it under `MongoQueryMode.NativeOnly` (proves it went
  native) AND `MongoQueryMode.DriverLinq` (proves the fallback still works), comparing to expected rows; follow the
  existing native test-class patterns (`NativeModeAssert`, in-memory oracle, ragged/null data where relevant).
- Existing tests that pin a now-intentionally-changed decline must be updated to the new behaviour, and listed in the
  report with justification. No other test changes.
- Preserve file BOMs; match surrounding style; nullable annotations; no public API change; `#if EF8/EF9/EF10` code
  compiles in all three configs. Update `Query/AGENTS.md` text that describes the changed rules (keep it terse, in
  its existing style).
- Verification: build Debug EF8/EF9/EF10 (0 errors, no new warnings); full test suites for all three
  (`dotnet test MongoDB.EFCoreProvider.sln -c "Debug <V>" --no-build`, MONGODB_URI/ATLAS_URI unset). Baseline at
  748111b1: 0 failures everywhere. A healthy suite takes ~5 min per config; if a run shows
  `TimeoutException`/"Connection refused" (dead testcontainer), it is invalid: kill and rerun that config. macOS has
  no `timeout` binary.
- Commit messages prefixed `EF-322: `. Fix rounds are new commits (no `--amend`).

### Task 1: One stored-serialization equivalence rule for anonymous-key joins and projected set ops

Today (in `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`):
- `IsStoredEqualityFaithfulKeyPair(IProperty, IProperty)` (anonymous composite join keys): scalar-only (value type or
  string, not primitive collection); equal `GetBsonRepresentation()`; equal `GetProviderClrType()`; effective converter
  `FindTypeMapping()?.Converter ?? GetValueConverter()`; converters equivalent iff both null, same instance, or same
  type AND that type is in the hand-kept `StatelessConverterTypes` list.
- `StoredSerializationsMatch(MongoFieldExpression?, MongoFieldExpression?)` (via `OperandSerializationsMatch`,
  projected Union/Concat/Intersect/Except operands): both default-serialized
  (`NativeGroupByBinder.HasDefaultKeySerialization`, a null field = computed value = default) → true; one null →
  false; same property → true; else BOTH must have NO BsonRepresentation and type-mapping converters of the same
  type, same `ProviderClrType`, and structurally equal `ConvertToProviderExpression` / `ConvertFromProviderExpression`
  (`ExpressionEqualityComparer`).

Required: one shared predicate deciding whether two properties are stored identically, used by both:
- equal `GetBsonRepresentation()` (both null, or equal — equal non-null representations now ACCEPTED for set ops);
- equal provider CLR type;
- effective converters (use the join's `FindTypeMapping()?.Converter ?? GetValueConverter()` form; confirm it agrees
  with what `BsonSerializerFactory` actually wraps for the set-op read path) equivalent iff both null, the same
  instance, or the same type with the same `ProviderClrType` and structurally equal to/from-provider expressions;
- delete `StatelessConverterTypes` / `IsStatelessConverterType` if nothing else uses them.
Keep caller-specific parts at the callers: the join keeps its scalar-only / primitive-collection check; the set op
keeps its both-default shortcut, its null-field (computed value) handling, and its TimeOfDay check. Put the shared
predicate where both callers (and future ones) can find it — e.g. next to `StoredOrdering` /
`NativeGroupByBinder.HasDefaultKeySerialization` in `Query/NativeTranslation`, or as a private static in QMTEV if no
better home; justify the choice. Rewrite both doc comments to describe the shared rule (the join doc's list of
stateless converters and "BoolToStringConverter("Y","N") vs ("T","F")" rationale must be re-expressed in terms of
the structural rule).

Tests first (FunctionalTests, next to the existing anonymous-key join and set-op serialization tests — grep for
tests exercising `IsStoredEqualityFaithfulKeyPair` / `OperandSerializationsMatch` behaviour, e.g. anonymous-key join
tests and set-op "converted vs unconverted" tests):
1. Anonymous-key join where one key member pair is mapped on both sides with identical lambda converters
   (`HasConversion(v => ..., v => ...)` written the same way on both entity types) → now native: NativeOnly returns
   the correct rows; DriverLinq also correct.
2. Projected set op (Union and Intersect at least) whose aliased properties carry the same non-default
   `[BsonRepresentation(...)]` (or fluent `HasBsonRepresentation`) on both operands → now native, correct rows
   under NativeOnly; DriverLinq too where the driver supports the shape (Intersect/Except have no driver oracle —
   NativeOnly + expected rows suffices there).
3. Must still decline (NativeOnly throws / Native falls back with correct rows where a fallback exists):
   (a) `BoolToStringConverter("Y","N")` vs `BoolToStringConverter("T","F")` — in BOTH a join pair and a set op.
   Before relying on the structural rule, VERIFY these two converters' expression trees differ (the constants must
   appear in the trees); if they don't differ, the structural rule is unsafe — stop and report BLOCKED with evidence;
   (b) different BsonRepresentations on the two sides; (c) converter vs no converter; (d) two lambda converters that
   capture a local variable (closures are distinct objects, so structural comparison must reject them — prove it).
4. Existing tests: run them; any that pinned the old set-op "any BsonRepresentation declines" or the join's
   "lambda converter declines" must be updated to the new behaviour (list them).
Commit: `EF-322: one stored-serialization equivalence rule for anonymous-key joins and set ops`.

### Task 2: Native collection Include over a multi-level join chain

`TranslateSelect` in QMTEV has the arm `TryGetCollectionIncludeOverJoinScope(selector) is { EntityExpression:
MemberExpression { Member.Name: "Outer" } }` (≈line 457), gated by `IsSingleEligibleNativeJoinScope`. It registers
only the LAST join's lookup (`Joins[^1]`), renames that join's alias to `<alias>_join` when it equals the Include
navigation's lookup alias, and marks one confirmation. With N ≥ 2 joins, N candidates vs 1 confirmation leaves
`HasUnconfirmedReferenceIncludeCandidate` true → Route = Fallback (correct results via driver-LINQ, not native).
Every sibling arm either requires `Levels.Count: 1` or calls `NativeJoinScopeProjectionBinder.ConfirmEntireChain`.

Required:
1. Confirm every level with `ConfirmEntireChain` (as the sibling chained arm does).
2. Root-only: the Include target must be the chain's root entity. At depth ≥2, `ti.Outer.Outer...` — require it to
   resolve to scope index 0 (e.g. via `NativeJoinScopeTranslator.TryResolveBareScopeLeaf`, as the chained bare-leaf
   arm does), not merely "member named Outer"; keep declining Inner-rooted Includes (the existing comment explains
   why).
3. Alias collisions at EVERY level: any join whose lookup alias equals the Include navigation's alias must not be
   collapsed with the Include's `$lookup` by `AddLookup` (the join needs `$unwind`, the Include the bare array —
   silently wrong data). The current rename is safe only because nothing reads the join's Inner side. At depth ≥2
   a LATER join may read an earlier join's output (its `localField` starts with `<alias>.` —
   `LookupExpression.ReadsOutputOf`), and projections/filters may read it too: renaming that level would break
   them. Either rename AND correctly rewrite every dependent reference, or — preferred unless trivially provable —
   DECLINE (MarkNotNativelyRepresentable) when a colliding level other than the last is read by anything. Make sure
   renamed aliases stay unique.
4. Paging: mirror the chained bare-leaf arm's guard — for `Levels.Count > 1`, decline when
   `HasPaging && HasPagingRecordedAfterAJoin` (paging between joins would be deferred past every `$lookup`).
   Keep single-level behaviour exactly as today.
5. Keep the "recognizer must not mutate then decline" rule: compute every decline condition before renaming or
   registering anything.

Tests first (FunctionalTests; find the existing collection-Include-over-join tests — grep for
`Include(` with `.Join(` in native test classes, and `ReferenceIncludeRecognizerTests` in UnitTests):
1. Two-level and three-level join chains with a collection Include on the root
   (e.g. `Customers.Include(c => c.Orders).Join(...).Join(...)`) → native under NativeOnly with correct rows
   (Include populated, join rows correct); DriverLinq also correct.
2. Collision case where an earlier (non-last) join resolves to the Include's navigation alias:
   (a) nothing reads it later → native & correct if you implement rename for that case, else declines with correct
   fallback rows; (b) a later join keys off that level's Inner → must not return wrong rows (declines, or native and
   correct).
3. Paging recorded between joins → declines (Native mode still correct rows via fallback).
4. Single-level shapes unchanged (existing tests).
Commit: `EF-322: native collection Include over multi-level join chains`.
