# Failing specification tests — Jira inventory

EF Core ships a "specification tests" suite that providers consume from the
`Microsoft.EntityFrameworkCore.Specification.Tests` NuGet package and override
to assert the provider's actual behavior. When the MongoDB provider does not
yet support the functionality exercised by a test, the override is updated to
**assert the current failure** rather than be skipped — so the test stays green,
and any change in behavior (different exception type, different error message,
or the feature beginning to work) is detected immediately.

Each such override is annotated with a `// Fails: <description> <ticket-id>`
comment. This document enumerates every ticket referenced by such comments
together with a one-line description, plus a section listing failure modes
that currently lack a ticket. Counts are sourced from `tests/MongoDB.EntityFrameworkCore.SpecificationTests/**/*.cs`.

> **Counting basis — read before trusting a number in the `Count` column.** Where a row is restated it
> means: the number of distinct overridden test methods carrying a `// Fails: … <ticket>` tag in
> `tests/MongoDB.EntityFrameworkCore.SpecificationTests/**/*.cs` (a method split across `#if` arms counts
> once). Most figures in the column predate that rule and **do not reconcile with it**: measured at
> `58e05a0`, EF-216 read 265 where the rule yields 217, EF-X020 read 168 against 171, EF-149 246 against
> 292, and EF-X002's 46-against-45 discrepancy was recorded earlier and never explained. No rule tried
> (raw tag lines, per-method, per-method-name, whole-`tests/`-tree) reproduces the older figures, so their
> basis is unknown. Rows touched from 2026-08-03 onwards state the rule's figure; the rest are left as
> they stand rather than silently re-derived.

> If you fix one of these bugs, search for the ticket id in the spec-tests
> project — the corresponding overrides need to be updated (drop the
> `AssertTranslationFailed` wrapper or the throws-asserting `Assert.Contains`,
> assert the new MQL baseline, and remove the `// Fails:` line).

---

## MongoDB EF Core provider tickets — `EF-NNN`

| Ticket | Comment subject | Description | Count |
| --- | --- | --- | --- |
| [EF-117](https://jira.mongodb.org/browse/EF-117) | _(no remaining `// Fails:` tags)_ | Cross-collection **Include**/`ThenInclude` is now implemented for the tested shapes. The five tests formerly tagged here were re-investigated: `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` (tracking + no-tracking) now **passes**; `Collection_include_over_result_of_single_non_scalar` and `Do_not_erase_projection_mapping_when_adding_single_projection` actually fail on cross-`DbSet` subquery translation (re-tagged **EF-X001**); `Included_one_to_many_query_with_client_eval` fails on driver client-evaluation (re-tagged **EF-X003**); and Include on a keyless entity (incl. multi-level) is a genuine PK-less `$lookup` gap (re-tagged **EF-X019**). EF-117 no longer has any active `// Fails:` tags. (Join/GroupJoin/SelectMany/RightJoin/subquery failures formerly tagged here were re-categorized — see EF-X001/EF-216/EF-220/EF-X016/EF-X017/EF-X018.) | 0 |
| [EF-149](https://jira.mongodb.org/browse/EF-149) | `GroupBy issue EF-149` | `GroupBy` translation is severely limited; most non-trivial group-by shapes fail to translate. | 246 |
| [EF-153](https://jira.mongodb.org/browse/EF-153) | `TagWith EF-153` | `TagWith(...)` content is silently dropped — does not appear in the emitted MQL. | 9 |
| [EF-164](https://jira.mongodb.org/browse/EF-164) | `Missing property values issue EF-164` / `Projections issue EF-164` | BSON documents that omit a required scalar (or required navigation) throw on materialization — `Project_root_with_missing_scalars`, `Project_root_entity_with_missing_required_navigation`, etc. | 3 |
| [EF-202](https://jira.mongodb.org/browse/EF-202) | `Entity equality issue EF-202` | Comparing two entities (`entity1 == entity2` / `Contains(entity)`) is not lowered to a key-equality comparison. | 4 |
| [EF-216](https://jira.mongodb.org/browse/EF-216) | `Cross-document navigation access issue EF-216` / `Navigations issue EF-216` | Navigations that cross collection boundaries cannot be translated; surfaces as `Unsupported cross-DbSet query between ...`. Documented at the helper `AssertNoMultiCollectionQuerySupport`. | 211 |
| [EF-217](https://jira.mongodb.org/browse/EF-217) | `Call ToString on DateTimeOffset EF-217` | `DateTimeOffset.ToString()` cannot be translated. | 2 |
| [EF-220](https://jira.mongodb.org/browse/EF-220) | `Multiple query roots issue EF-220` | Queries that reference more than one `DbSet<>` (Cartesian product / cross-join) are not translatable. Includes `SelectMany` across DbSets and tautology-predicate cross-joins. | 10 |
| [EF-221](https://jira.mongodb.org/browse/EF-221) | _(no remaining `// Fails:` tags)_ | `Equals` calls with genuinely mismatched, non-convertible simple types (e.g. `int?.Equals((ulong)x)`) are now folded to the correct constant result in `MongoEFToLinqTranslatingExpressionVisitor` instead of reaching the driver's `Equals` translator, which threw `InvalidCastException`. The four tests formerly tagged here now pass. | 0 |
| [EF-222](https://jira.mongodb.org/browse/EF-222) | `translation of Like issue EF-222` | `EF.Functions.Like(...)` is not translated. | 9 |
| [EF-227](https://jira.mongodb.org/browse/EF-227) | `Max over empty nullables issue EF-227` | `Min` / `Max` over an empty nullable sequence does not produce the EF-expected `null`. | 4 |
| [EF-228](https://jira.mongodb.org/browse/EF-228) | `Truncation data loss issue EF-228` | `Sum`/`Average` over `float` columns suffers precision/truncation loss when accumulated server-side. | 2 |
| [EF-232](https://jira.mongodb.org/browse/EF-232) | `Sum of empty set cast to nullable issue EF-232` | `Sum_with_no_data_cast_to_nullable` does not produce the EF-expected `null`. (The `Compiled_query_when_does_not_end_in_query_operator` failure that previously also cited EF-232 has been re-tagged as `EF-X011`.) | 1 |
| [EF-234](https://jira.mongodb.org/browse/EF-234) | `translation of Random issue EF-234` | `EF.Functions.Random()` is not translated. | 2 |
| [EF-235](https://jira.mongodb.org/browse/EF-235) | `Translate Convert methods issue EF-235` | `Convert.ToBoolean/Byte/Int*/Decimal/Double/String/...` calls are not translated. | 8 |
| [EF-237](https://jira.mongodb.org/browse/EF-237) | `MathF mapping issue EF-237` | `MathF.*` overloads (the `float` Math API) are not translated. | 25 |
| [EF-238](https://jira.mongodb.org/browse/EF-238) | `Math.Min/Math.Max mapping issue EF-238` | `Math.Min` / `Math.Max` are not translated. | 6 |
| [EF-239](https://jira.mongodb.org/browse/EF-239) | `Math.Sign mapping issue EF-239` | `Math.Sign` is not translated. | 1 |
| [EF-240](https://jira.mongodb.org/browse/EF-240) | `Double.RadiansToDegrees and Double.DegreesToRadians mapping issue EF-240` | `Double.RadiansToDegrees` / `Double.DegreesToRadians` are not translated. | 2 |
| [EF-241](https://jira.mongodb.org/browse/EF-241) | `Translate string.Trim methods issue EF-241` | `String.TrimStart(...)` / `String.TrimEnd(...)` with a `char[]` argument are not translated. | 6 |
| [EF-242](https://jira.mongodb.org/browse/EF-242) | `DateOnly support issue EF-242` | `DateOnly.FromDateTime(...)` (and related `DateOnly` conversions) are not translated. | 1 |
| [EF-243](https://jira.mongodb.org/browse/EF-243) | `StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243` | `string.StartsWith/Contains/EndsWith` with `StringComparison.Ordinal` or `OrdinalIgnoreCase` is not translated. | 9 |
| [EF-245](https://jira.mongodb.org/browse/EF-245) | `String.Join issue EF-245` | `String.Join(separator, source.Select(...))` is not translated. | 5 |
| [EF-246](https://jira.mongodb.org/browse/EF-246) | `DateTime subtraction issue EF-246` | `(dateA - dateB).TotalDays / TotalHours / TotalSeconds / TotalMilliseconds` is not translated. | 1 |
| [EF-247](https://jira.mongodb.org/browse/EF-247) | `Regex with non-constant pattern issue EF-247` | `Regex.IsMatch` with a non-constant pattern is not translated. | 1 |
| [EF-248](https://jira.mongodb.org/browse/EF-248) | `Translate String.FirstOrDefault and String.LastOrDefault issue EF-248` | `String.FirstOrDefault()` / `LastOrDefault()` (LINQ-on-string) is not translated. | 2 |
| [EF-249](https://jira.mongodb.org/browse/EF-249) | `checked issue EF-249` | `checked { ... }` arithmetic is not honored — the `Checked_context_with_arithmetic_does_not_fail` test sees a different exception than EF expects. | 1 |
| [EF-252](https://jira.mongodb.org/browse/EF-252) | `Concurrency detector tests broken EF-252` | `Throws_on_concurrent_query_first/list` — the concurrency detector does not fire as the EF base test expects. | 2 |
| [EF-253](https://jira.mongodb.org/browse/EF-253) | `Multiple ordering issue EF-253` | `OrderBy(x).ThenBy(x)` on the same column with different directions does not emit the expected MQL. | 1 |
| [EF-254](https://jira.mongodb.org/browse/EF-254) | `Take zero EF-254` | `.Skip(0).Take(0)` with a parameter does not produce the expected empty result. | 1 |
| [EF-371](https://jira.mongodb.org/browse/EF-371) | `returns wrong data (0 rows instead of 6) EF-371` | A self-referencing two-hop reference navigation (`e.Manager.Manager`) collapses to a single join and hop 2 degrades to an inner `$unwind`, so the query returns 0 rows instead of 6. Baselined green on EF10 by asserting the wrong-data failure; the EF8/EF9 arm is a translation failure tagged EF-X020. | 1 |

## MongoDB C# Driver tickets — `CSHARP-NNNN`

| Ticket | Comment subject | Description | Count |
| --- | --- | --- | --- |
| [CSHARP-5296](https://jira.mongodb.org/browse/CSHARP-5296) | `DateTimeOffset issue CSHARP-5296` | Driver-level: `DateTimeOffset.Now / UtcNow` component access (`.Year`, `.Hour`, etc.) is not translated by the LINQ provider. | 2 |
| [CSHARP-5836](https://jira.mongodb.org/browse/CSHARP-5836) | `Reverse not supported CSHARP-5836` | Driver-level: `Queryable.Reverse()` is not implemented in the driver's LINQ provider. | 14 |

## Upstream EF Core tickets — `dotnet/efcore#NNNNN`

| Ticket | Comment subject | Description | Count |
| --- | --- | --- | --- |
| [dotnet/efcore#36412](https://github.com/dotnet/efcore/issues/36412) | `EF upstream issue--see https://github.com/dotnet/efcore/issues/36412` | Upstream EF Core test bug — provider-side override compensates while the upstream is fixed. | 1 |

Two further GitHub references appear in the codebase but are not `// Fails:`-tagged (they are TODOs / notes, not failure annotations): [dotnet/efcore#36488](https://github.com/dotnet/efcore/issues/36488) (`NorthwindSetOperationsQueryMongoTest.cs:149`, upstream test has a bug), [dotnet/efcore#36521](https://github.com/dotnet/efcore/issues/36521) (`MongoApiConsistencyTest.cs:91`), [dotnet/efcore#36413](https://github.com/dotnet/efcore/issues/36413) (`Utilities/TestMqlLoggerFactory.cs:6`).

---

## Failure modes lacking a Jira ticket — proposed new tickets

These entries appear in `// Fails:` comments without an `EF-` or `CSHARP-` reference, or in test bodies as un-commented failure assertions. Each entry is assigned a **temporary** ticket id of the form `EF-X###` to be replaced with a real Jira number once filed; the `X` makes it obvious in `grep` results that the id is a placeholder.

| Temp ticket | Subject | Count |
| --- | --- | --- |
| EF-X001 | Sub-query selection across DbSets is not translated | 144 |
| EF-X002 | Provider throws a different exception than the EF translation-failure message | 46 |
| EF-X003 | Driver-level feature gaps surfaced as test failures | 19 |
| EF-X004 | Float `Sum`/`Average` truncation (likely duplicate of EF-228) | 1 |
| EF-X005 | BSON document missing nested required reference (AdHoc JSON) | 2 |
| EF-X006 | MongoDB `DateTimeKind` round-trip handling | 1 |
| EF-X007 | Views / `HasDefiningQuery` semantics for MongoDB collections | 2 |
| EF-X008 | No support for nested JSON in AdHoc JSON tests | 2 |
| EF-X009 | Single uncategorized failure — needs triage | 1 |
| EF-X010 | Provider-specific Include error message differs from EF baseline | 4 |
| EF-X011 | ~~Compiled query with non-query operator — wrong exception~~ — fixed by EF-233 | 0 |
| EF-X012 | `OfType` translation unsupported | 2 |
| EF-X013 | MongoDB has no `$xor` operator (`Where_bitwise_xor`) | 1 |
| EF-X014 | Server-side projection conflict with cast-to-nullable | 1 |
| EF-X015 | Sub-second `DateTime` component translation (nanosecond/microsecond) | 1 |
| EF-X016 | GroupJoin shapes not translated | 9 |
| EF-X017 | Join shapes not translated | 5 |
| EF-X018 | RightJoin not supported | 1 |
| EF-X019 | Include on keyless entity not supported (no primary key for $lookup join) | 2 |
| EF-X020 | Cross-collection Include/join/navigation not translated on EF8/EF9 (works on EF10) | 176 |
| EF-X021 | Filtered Include / query filter on cross-collection target not translated | 0 |
| EF-X022 | Join/GroupJoin whose inner source is a filtered or ordered sub-query is rejected | 18 |
| EF-X024 | `Where`/entity materialization over a flattened multi-join chain onto ambiguous same-navigation or navigation-less siblings is not translated | 2 |
| EF-X016 | Bulk `ExecuteUpdate`/`ExecuteDelete` source restricted to a single collection scoped by `Where` | 47 |

### EF-X001 — Sub-query selection across DbSets is not translated
Comment patterns: `// Fails: Subquery selection EF-X001`, `// Fails: Subqueries not supported EF-X001`, `// Fails: No subquery support EF-X001`.
Test-body patterns: `AssertTranslationFailed(() => base.X(...))`, `AssertNoMultiCollectionQuerySupport(() => base.X(...))`.
Affected: ~140 tests across `NorthwindAggregateOperators`, `NorthwindMiscellaneous`, `NorthwindWhere`, `NorthwindNavigations`, `NorthwindSetOperations`, `NorthwindJoin`, etc. Many overlap with [EF-216](https://jira.mongodb.org/browse/EF-216); a separate ticket lets cross-collection subquery support be tracked independently from raw navigation access.

### EF-X002 — Provider throws a different exception than the EF translation-failure message
Comment patterns: `// Fails: Not throwing expected translation failed exception from EF, but still throws EF-X002`, `// Fails: Not throwing expected translation failed exception from EF. EF-X002`, `// Fails: Does not throw expected unable to translate exception EF-X002`, `// Fails: Does not use translation failed message EF-X002`, `// Fails: Throws different exception, but still throws EF-X002`.
Affected: ~36 tests. EF's base tests expect `InvalidOperationException` with EF's "could not be translated" message; the provider currently throws `ExpressionNotSupportedException` / `MongoCommandException` / `NotSupportedException` instead. Aligning the messages would let EF-level diagnostics work without provider-specific overrides. Two of these are bulk-update shapes in `NorthwindBulkUpdatesMongoTest` — `Update_Concat_set_constant` / `Update_Union_set_constant`, asserted with `Assert.ThrowsAnyAsync<Exception>`. These are a **test-harness/transaction artifact rather than a bulk-path defect**: the conformance asserter's before/after snapshot query mirrors the `Concat`/`Union` source as `$unionWith`, which the server forbids inside the rollback transaction the fixture enlists (`MongoCommandException: Stage not supported inside of a multi-document transaction: $unionWith`) — so it fails before the provider's own clean bulk rejection. (The two GroupBy bulk shapes that previously sat here — `Delete_Where_predicate_with_GroupBy_aggregate`, `Delete_GroupBy_Where_Select_2` — now reject with the canonical translation-failure message via the bulk translation guard and are tracked under EF-X016.) Three further bulk shapes sit here for the same reason — `Update_with_join_set_constant` (EF9/EF10) and the EF10-only `Update_with_LeftJoin` / `Update_with_LeftJoin_via_flattened_GroupJoin`, asserted with `Assert.ThrowsAnyAsync<Exception>`: these `Join`/`LeftJoin`/`GroupJoin` sources became EF-translatable with the cross-collection-join work (EF-117), so the asserter's before/after snapshot reads the join (whose inner is a filtered subquery, not a bare collection) and the driver's LINQ provider throws `ExpressionNotSupportedException` before the bulk path's own rejection runs.

### EF-X003 — Driver-level feature gaps surfaced as test failures
Comment patterns: `// Fails: Unsupported by driver EF-X003`, `// Fails: Reverse not supported by driver EF-X003`, `// Fails: Limited support on client evaluation EF-X003`.
Affected: ~17 tests. These are MongoDB C# Driver gaps (the driver does not implement a particular LINQ-to-MQL translation). Many likely fold into existing `CSHARP-*` tickets; a single umbrella issue would let the provider track its dependency on driver work.

### EF-X004 — Float `Sum`/`Average` truncation (likely duplicate of EF-228)
Comment pattern: `// Fails: Truncation resulted in data loss EF-X004`.
Affected: 1 test (`NorthwindAggregateOperatorsQueryMongoTest.cs`). Almost certainly the same failure mode as [EF-228](https://jira.mongodb.org/browse/EF-228); recommend re-tagging with `EF-228` and dropping this ticket once confirmed.

### EF-X005 — BSON document missing nested required reference (AdHoc JSON)
Comment patterns: `// Fails: NestedRequiredReference is null in BsonDocument for entity id=6 EF-X005`, `// Fails: Entity id=5 has no RequiredReference field EF-X005`.
Affected: 2 tests in `AdHocJsonQueryMongoTest.cs`. Same family as [EF-164](https://jira.mongodb.org/browse/EF-164) but more specific — failure occurs when an owned navigation (rather than a scalar) is missing from the seeded BSON.

### EF-X006 — MongoDB `DateTimeKind` round-trip handling
Comment pattern: `// Fails: MongoDB DateTimeKind handling EF-X006`.
Affected: 1 test. BSON cannot represent `DateTimeKind.Unspecified`, so the provider normalizes to UTC; tests that compare original vs round-tripped `DateTime.Kind` diverge.

### EF-X007 — Views / `HasDefiningQuery` semantics for MongoDB collections
Comment patterns: `// Fails: Views are not supported, so this returns all entities from mapped collection. EF-X007`, `// Fails: Defining queries are not supported. EF-X007`.
Affected: 2 tests in `NorthwindKeylessEntitiesQueryMongoTest.cs`. The EF "view" / `HasDefiningQuery` notion doesn't map onto MongoDB; the provider returns the full collection instead of the view-filtered subset.

### EF-X008 — No support for nested JSON in AdHoc JSON tests
Comment pattern: `// Fails: No support for nested JSON EF-X008`.
Affected: 2 tests in `AdHocJsonQueryMongoTest.cs`. The provider's JSON-column emulation does not nest deeply enough for these EF Core AdHoc cases.

### EF-X009 — Single uncategorized failure — needs triage
Comment pattern: `// Fails: Unknown reasons EF-X009`.
Affected: 1 test. Author was unsure of root cause when adding the override.

### EF-X010 — Provider-specific Include error message differs from EF baseline
Pattern: `Include_collection_with_client_filter` across all four Include variants asserts
`Assert.Contains("ExpressionNotSupportedException", (await Assert.ThrowsAsync<ThrowsException>(...)).Message)`
— the same shape as the 28 existing `EF-X002` sites. The base test expects EF's own
`InvalidOperationException` translation-failed message for a client-evaluated member on an
Include-d query; the provider instead throws the driver's `ExpressionNotSupportedException`,
so `Assert.ThrowsAsync<InvalidOperationException>` inside the base method itself fails with a
`ThrowsException` reporting the actual type. The mechanism is identical to `EF-X002` — see that
entry for the general case; this one's trigger is specifically a client-evaluated member on an
`Include`, not a plain unsupported operator.
Affected: 4 tests (`NorthwindEFPropertyIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`, `NorthwindIncludeQueryMongoTest.cs`, `NorthwindStringIncludeQueryMongoTest.cs`).

Until this was fixed, each of these four suites shadowed the upstream `AssertTranslationFailed`
helper with a bare `try { await query(); } catch { return; }`, which — unlike the
`Assert.ThrowsAsync<ThrowsException>` pattern above — swallowed *any* exception, including an
assertion failure from wrong data returned by a query that didn't fail to translate at all. A
full audit of all 234 call sites of the shadowed helper (across the four suites) found this one
test as the only case actually masking a real failure; the other 230 call sites already threw a
genuine non-assertion exception and are unaffected. The shadowed helpers now delegate to a
corrected `MongoAssert.AssertTranslationFailed` (rejects any `XunitException`, so wrong data
still fails the test) instead of duplicating the swallow-everything logic in each file.

### EF-X011 — Compiled query with non-query operator — wrong exception — **fixed by [EF-233](https://jira.mongodb.org/browse/EF-233)**
Comment pattern (historical): `// Fails: Compiled query with non-query operator issue EF-X011`.
Root cause: `MongoQueryableMethodTranslatingExpressionVisitor` captured the "final" query-chain expression in its generic `Visit` override (`_finalExpression ??= expression`), which fires on whatever node is visited first. For a compiled query ending in a non-query operator (e.g. `.Count() == 1`), the wrapping `BinaryExpression` was visited before the actual `Count()` call, so the captured expression included the trailing comparison and downstream translation broke (`ArgumentException("No ultimate source found")`, or the reported `InvalidCastException` for other shapes). Fixed by moving the capture into `VisitMethodCall` (`_finalExpression ??= methodCallExpression`), which only ever fires on the true queryable-chain nodes. `NorthwindCompiledQueryMongoTest.Compiled_query_when_does_not_end_in_query_operator` now calls `base.*` directly and asserts the correct MQL baseline.

One incidental side effect: `NorthwindCompiledQueryMongoTest.Multiple_queries` (two separately-compiled queries against different `DbSet`s reusing translation state — a pre-existing [EF-216](https://jira.mongodb.org/browse/EF-216) cross-DbSet limitation) now surfaces as an `ArgumentException` from the driver-LINQ rebuild instead of the provider's own "Unsupported cross-DbSet query" guard. The test was updated to assert the new exception type; per this repo's versioning conventions, the exception type for an already-unsupported shape is not part of the public contract.

### EF-X012 — `OfType` translation unsupported
Comment pattern: `// Fails: OfType translation EF-X012`.
Affected: 2 tests in `NorthwindAggregateOperatorsQueryMongoTest.cs` (`OfType_Select`, `OfType_Select_OfType_Select`). `Queryable.OfType<T>()` is not translated; the failure surfaces as a generic `AssertTranslationFailed`.

### EF-X013 — MongoDB has no `$xor` operator
Comment pattern: `// Fails: MongoDB does not have an xor operator EF-X013`.
Affected: 1 test (`NorthwindWhereQueryMongoTest.Where_bitwise_xor`). The provider throws `ExpressionNotSupportedException` with message `"because MongoDB does not have a boolean $xor operator"`.

### EF-X014 — Server-side projection conflict with cast-to-nullable
Comment pattern: `// Fails: Server-side projection conflict with cast-to-nullable EF-X014`.
Affected: 1 test (`NorthwindSelectQueryMongoTest.Select_bool_closure_with_order_by_property_with_cast_to_nullable`). Server rejects the generated `$project` stage with `"Cannot do exclusion on field _key1 in inclusion projection"` — likely a translator bug where the projection stage mixes inclusion and exclusion.

### EF-X015 — Sub-second `DateTime` component translation (nanosecond/microsecond)
Comment pattern: `// Fails: Sub-second DateTime component translation EF-X015`.
Affected: 1 test (`NorthwindMiscellaneousQueryMongoTest.Where_nanosecond_and_microsecond_component`). `DateTime.Nanosecond` and `DateTime.Microsecond` (added in .NET 7) are not translated; the provider throws `ExpressionNotSupportedException`.

### EF-X016 — Bulk `ExecuteUpdate`/`ExecuteDelete` source restricted to a single collection scoped by `Where`
Comment pattern: `// Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; <shape> unsupported EF-X016`.
Test-body pattern: `AssertTranslationFailed(() => base.X(...))`.
Affected: 47 tests in `NorthwindBulkUpdatesMongoTest.cs`. By design, the provider supports bulk `ExecuteUpdate`/`ExecuteDelete` only against a single collection scoped by `Where`, with constant / parameter / self-referencing scalar setters (see README "What is supported" and EF-107). The EF conformance suite exercises shapes outside that subset — joins / correlated subqueries, set operations (`Except`/`Intersect`), `GroupBy`, `SelectMany`, cross-document navigation predicates, non-entity projection sources, and multiple-collection updates — and the provider rejects them during translation with an `InvalidOperationException` whose message reports the LINQ expression could not be translated. This is a documented limitation rather than a bug to fix; the ticket tracks the boundary so any future expansion of the supported subset (or a behavior change) is detected. Most shapes are rejected by compile-time bulk-source validation; a few (e.g. a `GroupBy` subquery inside the `Where` predicate) only fail when the filter is built at execution time, where a translation guard (`TranslateBulkOrThrow` in `MongoShapedQueryCompilingExpressionVisitor`) converts the raw driver/expression exception into the same canonical non-query translation failure. A handful of sibling shapes surface differently and are tagged separately: cross-DbSet `GroupBy` rejections under [EF-216](https://jira.mongodb.org/browse/EF-216), and the `$unionWith`-in-transaction `Concat`/`Union` cases under `EF-X002`.

**Remaining unsupported shapes — grouped by root cause.** The unifying cause: two-phase execution only works when the *source query is translatable as a read*, because phase 1 must be able to project the target `_id`s. Every group below fails because the source shape itself cannot be translated — so neither phase 1 nor a single-command filter can be built. (Delete has the analogous cases; the `Update_*` methods are enumerated here since they are the larger set.)

- **Joins / correlated subqueries** — `RightJoin`, cross-apply, outer-apply, and the cross-/two-collection join shapes the read path cannot translate, so phase 1 cannot project `_id`s and the source is rejected at translation with EF's canonical message. Unblocking requires net-new read-side join translation (ultimately a driver `$lookup`/`$group` + `$merge` capability) and is out of scope for EF-107. Update cases: `Update_Where_Join_set_property_from_joined_single_result_scalar`, `Update_Where_Join_set_property_from_joined_single_result_table`, `Update_Where_Join_set_property_from_joined_table`, `Update_with_two_inner_joins`, `Update_with_left_join_set_constant`, `Update_with_cross_join_set_constant`, `Update_with_cross_apply_set_constant`, `Update_with_outer_apply_set_constant`, `Update_with_cross_join_cross_apply_set_constant`, `Update_with_cross_join_left_join_set_constant`, `Update_with_cross_join_outer_apply_set_constant`, `Update_with_PK_pushdown_and_join_and_multiple_setters`, plus EF10-only `Update_with_RightJoin`. (The simple single-key `Join` / `LeftJoin` / `GroupJoin` shapes — `Update_with_join_set_constant` and EF10-only `Update_with_LeftJoin` / `Update_with_LeftJoin_via_flattened_GroupJoin` — became read-translatable with the cross-collection-join work (EF-117), so they no longer fail at bulk translation; the conformance asserter's snapshot read of the filtered-inner join now throws a driver non-translation exception instead, tracked under EF-X002.)
- **Set operations — `Except` / `Intersect`** — MongoDB has no cross-collection intersect/except (only `$unionWith`), so the source cannot be expressed and is rejected at translation. Update cases: `Update_Except_set_constant`, `Update_Intersect_set_constant`.
- **`SelectMany`** — the read path has no cross-document flattening, so the source cannot be translated. Update cases: `Update_Where_SelectMany_set_null`, `Update_Where_SelectMany_subquery_set_null`.
- **Cross-document navigation predicates** — predicates traversing navigations across collections aren't translatable. Update cases: `Update_Where_using_navigation_2_set_constant`, `Update_Where_using_navigation_set_null`.
- **Multiple-collection update** — a relational table-sharing concept with no document-model meaning; rejected by design regardless of phase strategy. Update case: `Update_multiple_tables_throws`.

**Related shapes that surface as a different failure mode** (cross-referenced, not under the canonical translation-failure pattern above):

- **`Concat` / `Union` (`EF-X002`)** — these lower to `$unionWith`, which the server forbids inside the multi-document transaction the conformance asserter (and the two-phase path) run in, so they throw a runtime `MongoCommandException` *before* the provider's clean bulk rejection. Update cases: `Update_Concat_set_constant`, `Update_Union_set_constant`. Unblocking requires the server to allow `$unionWith` inside a transaction.
- **`GroupBy`-scoped (`EF-216`)** — `GroupBy` isn't translatable; these surface as the provider's `Unsupported cross-DbSet query` rejection and are asserted via `AssertNoMultiCollectionQuerySupport` rather than a `// Fails:` `AssertTranslationFailed`. Update cases: `Update_Where_GroupBy_First_set_constant`, `Update_Where_GroupBy_First_set_constant_2`, `Update_Where_GroupBy_First_set_constant_3`, `Update_Where_GroupBy_aggregate_set_constant`.

**Two-phase bulk support (EF-107):** `OrderBy`/`Skip`/`Take`/`Distinct`-scoped bulk delete and update are now supported via a transactional two-phase execution strategy: phase 1 projects the matching `_id` values (including composite-key entities) into a temporary in-memory set; phase 2 issues the actual `deleteMany`/`updateMany` scoped to that `_id` set. The corresponding conformance tests (`Delete_Where_OrderBy*`, `Delete_Where_Skip*`, `Delete_Where_Take*`, `Delete_Where_Distinct`, `Update_Where_OrderBy*`, `Update_Where_Skip*`, `Update_Where_Take*`, `Update_Where_Distinct_set_constant`, `Delete_Where_Skip_Take_Skip_Take_causing_subquery`, `Update_Where_OrderBy_Skip_Take_Skip_Take_set_constant`) have been promoted from `AssertTranslationFailed` to `base.*` and are no longer tracked under this ticket.

### EF-X016 — GroupJoin shapes not translated
Comment pattern: `// Fails: GroupJoin shape not translated EF-X016`.
Affected: 9 tests in `NorthwindJoinQueryMongoTest.cs` — `GroupJoin_aggregate_anonymous_key_selectors` (+`2`, +`_one_argument`, +`_nested`), `GroupJoin_DefaultIfEmpty_multiple`, `GroupJoin_DefaultIfEmpty2`, `GroupJoin_subquery_projection_outer_mixed`, `GroupJoin_on_true_equal_true` (EF9 only), `Unflattened_GroupJoin_composed_2`. These are `GroupJoin` shapes the flatten-to-`$lookup` pipeline does not yet handle (aggregate / anonymous or nested key selectors, multiple `DefaultIfEmpty`, on-`true == true`, mixed projection). Distinct from the `GroupJoin` *subquery* shapes, which are tracked under [EF-X001](#ef-x001--sub-query-selection-across-dbsets-is-not-translated), and from cross-collection `Include`, which is [EF-117](https://jira.mongodb.org/browse/EF-117).

### EF-X017 — Join shapes not translated
Comment pattern: `// Fails: Join shape not translated EF-X017`.
Affected: 5 tests — `Join_composite_key`, `Join_complex_condition`, `Join_with_key_selectors_being_nested_anonymous_objects`, `Join_local_collection_int_closure_is_cached_correctly` (all in `NorthwindJoinQueryMongoTest.cs`) and `Join_with_default_if_empty_on_both_sources` (in `NorthwindMiscellaneousQueryMongoTest.cs`). The provider translates simple single-key `Join`/`GroupJoin` to `$lookup`, but composite keys, complex (non-equality) conditions, nested-anonymous key selectors, joins against a local in-memory collection, and `DefaultIfEmpty` on both sources are not yet supported.

### EF-X018 — RightJoin not supported
Comment pattern: `// Fails: RightJoin not supported EF-X018`.
Affected: 1 test (`NorthwindJoinQueryMongoTest.RightJoin`, EF10+). `Queryable.RightJoin` (the EF9+/EF10 operator) is not translated; the provider fails translation rather than emitting the reversed `$lookup` pipeline.

### EF-X019 — Include on keyless entity not supported (no primary key for $lookup join)
Comment pattern: `// Fails: Include on keyless entity not supported (no primary key for $lookup join) EF-X019`.
Affected: 2 tests (`NorthwindKeylessEntitiesQueryMongoTest.KeylessEntity_with_included_nav`, `KeylessEntity_with_included_navs_multi_level`). A keyless entity has no primary key, so the cross-collection `$lookup` join-key cannot be resolved and keyless entities are never tracked (no `InternalEntityEntry` is emitted into the shaper). The provider now detects this in `MongoProjectionBindingRemovingExpressionVisitor.AddInclude` and throws the standard `CoreStrings.TranslationFailed` (translation-failure) message instead of the internal `Sequence contains no matching element` error. Distinct from cross-collection `Include` on keyed entities, which is implemented (formerly [EF-117](https://jira.mongodb.org/browse/EF-117)).

### EF-X020 — Cross-collection Include/join/navigation not translated on EF8/EF9
Comment pattern: `// Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020`.
Test-body pattern: the override is wrapped in `#if EF8 || EF9` / `#else`. The `#if EF8 || EF9` branch asserts the translation failure (`AssertTranslationFailed(() => base.X(...))`); the `#else` branch keeps the working EF10 baseline (the real `base` call plus its `AssertMql(...)`).
Affected: 176 tests across `NorthwindEFPropertyIncludeQueryMongoTest`, `NorthwindStringIncludeQueryMongoTest`, `NorthwindIncludeQueryMongoTest`, `NorthwindIncludeNoTrackingQueryMongoTest`, `NorthwindNavigationsQueryMongoTest`, `NorthwindMiscellaneousQueryMongoTest`, `NorthwindJoinQueryMongoTest`, `NorthwindAggregateOperatorsQueryMongoTest`, `NorthwindAsNoTrackingQueryMongoTest`, `NorthwindKeylessEntitiesQueryMongoTest`, `NorthwindSelectQueryMongoTest`, `NorthwindSetOperationsQueryMongoTest`, `NorthwindWhereQueryMongoTest`, and `BuiltInDataTypesMongoTest`. These are the cross-collection Include/`ThenInclude`/join/navigation shapes implemented for the EF10-targeted query pipeline. On EF8/EF9 the upstream nav-expansion / query pipeline produces a different expression shape (e.g. an extra `.OrderBy(o => o.OrderID)` injected during navigation expansion) that the EF10-targeted translator does not handle, so translation fails with EF Core's `InvalidOperationException` "could not be translated" (a few also throw the provider's `ExpressionNotSupportedException`); the same query translates and runs on EF10. The provider's local `AssertTranslationFailed` helper swallows whichever exception is thrown, so both shapes are covered. Four `Include_reference_dependent_already_tracked` overrides (in the four Include suites) emit MQL from a first principal query *before* the Include sub-query fails to translate, so their `#if EF8 || EF9` branch asserts only the translation failure and omits the empty `AssertMql()`. `BuiltInDataTypesMongoTest.Can_read_back_bool_mapped_as_int_through_navigation` is split three ways (a nested `#if EF9` inside the file's `#if !EF8` branch, plus the `#else` EF8 branch) because that file uses async signatures on EF9/EF10 and sync `void` signatures on EF8.

### EF-X022 — Join/GroupJoin whose inner source is a filtered or ordered sub-query is rejected
Comment pattern: `// Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022`.
Test-body patterns: `Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>` asserting
`"Expression not supported"`, or `Assert.ThrowsAnyAsync<Exception>` where the EF8/EF9 failure mode differs.

The provider lowers a single-key `Join`/`GroupJoin` to `$lookup`. When the **inner** source is not a bare
collection but a sub-query — `Orders.Where(...)`, `Orders.OrderBy(...)`, or a dependent-side query filter
that lowers to the same `Where` — the driver's LINQ provider rejects the whole expression with
`ExpressionNotSupportedException: ... because expression must be a MongoDB IQueryable against a collection`.
This is an **intentional rejection**: these shapes were previously folded into the correlated `$lookup`
sub-pipeline, which is not a faithful translation — the mis-folding formerly tracked as `CSHARP-6017`.
Failing loudly is preferred over returning wrong results, so this is a documented boundary rather than a
regression to fix in the test suite.

Affected: 18 overrides, all asserting the rejection (none are `Skip`ped).

- `NorthwindJoinQueryMongoTest` — `Join_customers_orders_with_subquery`,
  `Join_customers_orders_with_subquery_predicate`, `Join_customers_orders_with_subquery_anonymous_property_method`,
  `GroupJoin_customers_employees_subquery_shadow`, `GroupJoin_SelectMany_subquery_with_filter`, plus
  EF10-only `GroupJoin_DefaultIfEmpty2` and `GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty`.
- `NorthwindMiscellaneousQueryMongoTest` — `OrderBy_Join`, `Join_take_count_works`.
- `NorthwindQueryFiltersQueryMongoTest` (EF10-only) — `Entity_Equality`, `Included_many_to_one_query`,
  `Included_many_to_one_query2`, where the *query filter* on the `$lookup` target is what makes the inner
  a filtered sub-query.
- **Former `CSHARP-6017` skips**, now converted to the same assertion style rather than
  `[ConditionalTheory(Skip = ...)]` — `Join_customers_orders_with_subquery_with_take`,
  `Join_customers_orders_with_subquery_predicate_with_take`,
  `Join_customers_orders_with_subquery_anonymous_property_method_with_take`, `GroupJoin_simple_subquery`,
  `GroupJoin_Subquery_with_Take_Then_SelectMany_Where`, `GroupJoin_customers_employees_subquery_shadow_take`.
  These were skipped while the driver silently mis-folded the `Take`/sub-query inner and returned wrong
  results; the driver now rejects them, so they assert the rejection and no longer need a skip.

Interaction with [EF-352](https://jira.mongodb.org/browse/EF-352): that fix (shadow/scalar `EF.Property`
read on a joined entity in a projection) re-enabled
`Join_customers_orders_with_subquery_anonymous_property_method` against driver 3.10, but the query's inner
is an ordered sub-query, so 3.11 rejects it and the override now asserts the rejection instead. The
underlying EF-352 fix is still covered functionally by
`ShadowPropertyJoinProjectionTests` and `ShadowPropertyFlatJoinProjectionTests`; the first of those had the
`orderby` dropped from its join inner (incidental to what it asserts) so it keeps exercising the non-root
joined-entity shaper on a shape the driver still supports.

The EF10-only entries carry a second ticket for their EF8/EF9 failure mode (`EF-X016`, `EF-X001`, `EF-202`,
`EF-216`), because on those versions the query fails earlier in nav-expansion and logs no MQL at all —
hence the `#if EF8 || EF9` split on the `AssertMql` baseline.

Lifting this ticket requires translating the inner sub-query into the `$lookup` sub-pipeline correctly
(correlated `let`/`pipeline` form), which overlaps with [EF-X021](#ef-x021--filtered-include--query-filter-on-cross-collection-target-not-translated).

### EF-371 — self-referencing two-hop reference navigation collapses to one join

**History (kept deliberately — the original diagnosis was wrong).** This entry replaces
"EF-216 — wrong-data on EF10 (cross-collection navigation), unsupported on EF8/EF9", which covered
**five** `NorthwindNavigationsQueryMongoTest` methods skipped with
`[ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]`:
`Include_with_multiple_optional_navigations`, `Multiple_include_with_multiple_optional_navigations`,
`Navigation_from_join_clause_inside_contains`, `Navigation_inside_contains_nested` and
`Select_Where_Navigation_Null_Deep`. They were believed to share one root cause — "compound multi-hop
navigation lowering". They did not.

**All five are now fixed.** Four via EF-369 / EF-370 (see
`docs/superpowers/specs/2026-08-03-required-nav-unwind-semantics-design.md`): the composed predicate /
`Contains` filter was being *discarded* when a multi-join Include chain was flattened to root-level
`_lookup_<Nav>` fields, so the query returned every row (2155 against 112 / 112 / 352 / 40 expected).
Those four are un-skipped, run on EF10 with real `AssertMql` baselines, and take the standard
`#if EF8 || EF9` **EF-X020** arm (their navigations are optional, so EF lowers them to `Queryable.LeftJoin`,
whose dispatch case does not exist before EF10):

- `Include_with_multiple_optional_navigations`
- `Multiple_include_with_multiple_optional_navigations`
- `Navigation_from_join_clause_inside_contains`
- `Navigation_inside_contains_nested`

The fifth, `Select_Where_Navigation_Null_Deep` (a self-referencing two-hop navigation,
`e.Manager.Manager == null`), was a **different defect**, tracked and fixed as EF-371. It filters on
`e.Manager.Manager == null` over the self-referencing `Employee.Manager` navigation and was returning
**0 rows where 6 are correct**. Two causes, both independent of the discarded-operator bug fixed by
EF-369/EF-370:

1. `MongoQueryExpression._innerCollections` is keyed by `IEntityType`, so a self-referencing two-hop
   navigation registers only **one** inner collection. `InnerCollections.Count > 1` stays false, no
   forced-unwind lookups are registered, and the query never reaches the flat `_lookup_*` path — it stays
   on the chained driver-native join path.
2. On that path `RewriteJoinNode`'s guard `if (isLeftJoin && shapedPath && outerType == oldOuterType)`
   builds the preserving pipeline only for the **first** hop; at hop 2 the outer type is already a
   `LeftJoinResult`, so it falls through to `Queryable.Join` and the driver emits a bare `$unwind` — an
   **inner** join. Rows whose grandparent is absent are dropped, so `== null` can never match.

The fix: `_innerCollections` now registers per-navigation (not just per-`IEntityType`), lookup-alias
collisions are detected by the alias string a plain lookup would produce rather than by `INavigation`
identity (so two distinct navigations sharing a name are disambiguated too), and the residual `Where`
left over from stripping a join chain for `$lookup` is rewritten against the flattened
`_lookup_<Navigation>` fields instead of being discarded (the sibling `Select` is still discarded — its
projection is reconstructed independently by the compiled client-side shaper). It is un-skipped and
asserts real data + MQL on EF10; EF8/EF9 still assert translation failure per EF-X020.

With all five un-skipped, the full spec suite is **green on all three EF versions** (EF8/EF9/EF10:
0 failures) and carries **no skips for this work**.

---

### EF-X021 — Filtered Include / query filter on cross-collection target not translated
Comment pattern: `// Fails: Filtered Include / query-filter predicate on a cross-collection $lookup target is not translated EF-X021`.
Affected: 0 spec overrides today (no specification test currently exercises a *filtered* cross-collection Include
with a predicate, nor a `HasQueryFilter` on a cross-collection dependent — `Filtered_include_with_multiple_ordering`
only uses OrderBy/Skip/Take, which *are* translated). Tracked because the provider now **fails loudly** for these
shapes instead of silently dropping the predicate. A user filtered-Include predicate
(`.Include(c => c.Orders.Where(o => ...))`) and a dependent-side `HasQueryFilter` (soft-delete / multi-tenant) both
lower to a `Where` inside the collection-Include subquery; that `Where` is **not** the synthetic FK-correlation join
condition and is not yet translated into the `$lookup` sub-pipeline `$match`. Previously it was silently dropped
(returning *all* dependents and bypassing the filter — wrong data); the provider now throws a translation failure
(`CoreStrings.TranslationFailed`). Translating the predicate into the sub-pipeline `$match` is the follow-up feature
this ticket tracks. Functional coverage:
`CrossCollectionIncludeTests.Filtered_collection_include_predicate_is_not_silently_dropped` and
`CrossCollectionIncludeTests.Query_filter_on_collection_include_target_is_not_silently_dropped`.

### EF-X024 — `Where`/entity materialization over a flattened multi-join chain onto ambiguous same-navigation or navigation-less siblings is not translated
Comment pattern: `// Fails: Where over a flattened multi-join chain is not translated EF-X024`, or
`// Fails: two navigation-less same-target-type joins chained off root, ... EF-X024`.
Affected: 2 spec overrides — `NorthwindJoinQueryMongoTest.Join_GroupJoin_DefaultIfEmpty_Where` (EF10 branch;
the EF8/EF9 branch rejects the whole shape under EF-X020, so nothing is masked there) and
`NorthwindJoinQueryMongoTest.Join_same_collection_multiple`.
Once a query has two or more cross-collection joins it is emitted in **flat** mode (one root-level
`$lookup` + `$unwind` per join, `MongoQueryExpression.UsesDriverJoinFields == false`). A composed operator
(`Where`, a projecting `Select`, etc.) wrapped around the join chain is reattached onto the flattened
`_lookup_<Navigation>` fields by `MongoEFToLinqTranslatingExpressionVisitor.StripJoinForLookup` /
`ReattachComposedOperator`, retargeting `TransparentIdentifier` `.Outer`/`.Inner` member accesses via
`TransparentIdentifierToLookupFieldRewriter`. That reattachment is now the general case — see
`SameTargetTypeJoinTests.Filter_after_a_flattened_multi_join_chain_is_applied_not_dropped`, which asserts a
`Where` placed after **two** joins (same-typed and different-typed chains) is genuinely applied, not
dropped or rejected.
The ONE shape that still fails is two INDEPENDENT (non-chained) joins that resolve the SAME navigation with
the SAME foreign-key property — e.g. `Join_GroupJoin_DefaultIfEmpty_Where`'s two plain joins from
`Customer` to `Order` on `CustomerID`. `TransparentIdentifierToLookupFieldRewriter.ResolveLookup`'s
ambiguity fallback disambiguates by structural position (a self-referencing CHAIN, where a later hop's
`$lookup.localField` is prefixed by an earlier hop's alias — see EF-372); two siblings with no
chaining/prefix relationship between them can't be told apart that way, so `StripJoinForLookup` declines
(returns `null`) and the join chain would otherwise survive to be rendered by the driver's native
`LeftJoin` support — which itself only handles a single cross-collection join and would silently
double-nest under a second `_outer`, materializing null entities. `GuardAgainstUnstrippableMultiJoin`
(`MongoEFToLinqTranslatingExpressionVisitor`) catches exactly that combination (`stripped == null` with 2+
forced-unwind lookups already registered) and fails translation loudly instead of falling back to that
broken native rendering. Disambiguating true siblings — the remaining piece this ticket tracks — needs a
way to tell two same-navigation, same-FK, non-chained joins apart (e.g. by originating LINQ join-clause
identity) that `ResolveLookup` doesn't have today.

`Join_same_collection_multiple` hits the same `GuardAgainstUnstrippableMultiJoin` decline via a different
route: two plain (inner) `Join` calls onto `Customer` with **no corresponding model navigation** at all
(a bare key-equality `Join`, not `Include`-driven). A navigation-less hop's `$lookup` (built directly from
the raw outer/inner key property paths — see EF-377) still registers as a forced-unwind lookup and still
triggers flat mode, but `StripJoinForLookup`/`ResolveLookup` key their matching on `LookupExpression.Navigation`
and structural position, which a bare hop can't fully disambiguate either, so the strip declines here too.
Unlike the `Where`-over-a-scalar-projection case this ticket originally tracked, this query materializes a
raw entity (`c3`) directly — the guard's original narrowing (reject only when a declined join is
left-outer, since a scalar/anonymous projection's baked-in field paths tolerate native-rendered inner-join
chains) doesn't hold for an entity-shaped result: entity materialization always reads through the
`_lookup_<Alias>`/`_inner` alias keyed by `MongoQueryExpression.UsesDriverJoinFields`, which the native
fallback doesn't produce once 2+ lookups are registered, regardless of join kind. The guard now rejects
every declined multi-lookup shape when the result is entity-shaped (reached via `Translate`, not
`TranslateProjected`), not only the left-outer ones.

## Audit findings — tagging hygiene

The following inconsistencies were observed while building this inventory and have since been resolved.

### 1. `EF-232` was reused for two distinct failure modes — **fixed**

`Sum_with_no_data_cast_to_nullable` kept `EF-232` (since fixed and removed from the table above); `Compiled_query_when_does_not_end_in_query_operator` was re-tagged with the new temp ticket `EF-X011`.

### 2. Sibling `#if` branches missing the `// Fails:` tag — **fixed**

Both branches of `Any_on_distinct`, `Contains_on_distinct`, `All_on_distinct`, `IQueryable_captured_variable`, and `Where_Order_First` in `NorthwindMiscellaneousQueryMongoTest.cs` now carry the `EF-216` tag. (`Filtered_include_with_multiple_ordering` in `NorthwindStringIncludeQueryMongoTest.cs` was verified to pass — it does not need a tag despite the sibling-file convention; the audit's flag was a false positive there.)

### 3. Duplicate `EF-243` references on the same method — **fixed**

The duplicate inline `// ... See EF-243.` mentions in the three StartsWith/Contains/EndsWith overrides were dropped; the `// Fails: ... EF-243` lines remain.

### 4. Single-mode helpers tag at the helper, not at each call site — **fixed**

The convention: when a helper method wraps a single failure mode, the `// Fails:` line goes above the helper declaration, not at every call site. Applied to:

- `AssertNoMultiCollectionQuerySupport` (single mode: `EF-216`) — definition now tagged in every file that declares it; call sites left untagged. This was already the dominant pattern in 8 of 9 files; the one missing tag in `NorthwindGroupByQueryMongoTest.cs` was added.
- `AssertGroupByUnsupported` (single mode: `EF-149`) — already tagged at the helper.

The generic `AssertTranslationFailed` helper is **not** single-mode (it's used for many distinct failure causes) and is therefore tagged per call site.

### 5. Untagged `AssertTranslationFailed` and Throws callers — **fixed**

All ~70 `AssertTranslationFailed` call sites now carry a `// Fails:` tag — categorized as `EF-117` (joins/Include), `EF-149` (GroupBy), `EF-216` (cross-document), `EF-X001` (sub-query selection), or one of the new EF-X011–EF-X015 tickets. Three additional `Assert.Throws*` blocks (`Type_casting_inside_sum`, `Late_subquery_pushdown`, `Where_nanosecond_and_microsecond_component`) were also tagged. Two malformed tags (`// Fails ` without colon) were corrected. `Assert.Throws<...>` blocks that preserve EF Core's own expected throws (`Max_on_empty_sequence_throws`, `Client_code_using_instance_*_throws`, `VectorSearch_throws_if_num_candidates_set_for_exact_search`) intentionally remain untagged — they assert the provider matches EF behavior, not that it fails.

### Verification

After all edits:

```
grep -rEho "(EF-X?[0-9]+|CSHARP-[0-9]+)" tests/.../SpecificationTests --include="*.cs" \
  | sort | uniq -c | sort -rn
```

returns 50 distinct ticket ids, including 16 temporary `EF-X###` placeholders (`EF-X016` was
added for the `NorthwindBulkUpdatesMongoTest` out-of-subset rejections). The static audit (lookback-5 search for `// Fails:` above any `AssertTranslationFailed` / `Assert.Throws*` / `AssertGroupByUnsupported` callsite, excluding helper definitions and single-mode helper callers) reports zero remaining holes among Mongo-failure cases.
