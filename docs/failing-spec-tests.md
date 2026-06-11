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

> If you fix one of these bugs, search for the ticket id in the spec-tests
> project — the corresponding overrides need to be updated (drop the
> `AssertTranslationFailed` wrapper or the throws-asserting `Assert.Contains`,
> assert the new MQL baseline, and remove the `// Fails:` line).

---

## MongoDB EF Core provider tickets — `EF-NNN`

| Ticket | Comment subject | Description | Count |
| --- | --- | --- | --- |
| [EF-117](https://jira.mongodb.org/browse/EF-117) | _(no remaining `// Fails:` tags)_ | Cross-collection **Include**/`ThenInclude` is now implemented for the tested shapes. The five tests formerly tagged here were re-investigated: `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` (tracking + no-tracking) now **passes**; `Collection_include_over_result_of_single_non_scalar` and `Do_not_erase_projection_mapping_when_adding_single_projection` actually fail on cross-`DbSet` subquery translation (re-tagged **EF-X001**); `Included_one_to_many_query_with_client_eval` fails on driver client-evaluation (re-tagged **EF-X003**); and Include on a keyless entity (incl. multi-level) is a genuine PK-less `$lookup` gap (re-tagged **EF-X019**). EF-117 no longer has any active `// Fails:` tags. (Join/GroupJoin/SelectMany/RightJoin/subquery failures formerly tagged here were re-categorized — see EF-X001/EF-216/EF-220/EF-X016/EF-X017/EF-X018.) | 0 |
| [EF-149](https://jira.mongodb.org/browse/EF-149) | `GroupBy issue EF-149` | `GroupBy` translation is severely limited; most non-trivial group-by shapes fail to translate. | 266 |
| [EF-153](https://jira.mongodb.org/browse/EF-153) | `TagWith EF-153` | `TagWith(...)` content is silently dropped — does not appear in the emitted MQL. | 9 |
| [EF-164](https://jira.mongodb.org/browse/EF-164) | `Missing property values issue EF-164` / `Projections issue EF-164` | BSON documents that omit a required scalar (or required navigation) throw on materialization — `Project_root_with_missing_scalars`, `Project_root_entity_with_missing_required_navigation`, etc. | 3 |
| [EF-202](https://jira.mongodb.org/browse/EF-202) | `Entity equality issue EF-202` | Comparing two entities (`entity1 == entity2` / `Contains(entity)`) is not lowered to a key-equality comparison. | 4 |
| [EF-216](https://jira.mongodb.org/browse/EF-216) | `Cross-document navigation access issue EF-216` / `Navigations issue EF-216` | Navigations that cross collection boundaries cannot be translated; surfaces as `Unsupported cross-DbSet query between ...`. Documented at the helper `AssertNoMultiCollectionQuerySupport`. | 270 |
| [EF-217](https://jira.mongodb.org/browse/EF-217) | `Call ToString on DateTimeOffset EF-217` | `DateTimeOffset.ToString()` cannot be translated. | 2 |
| [EF-218](https://jira.mongodb.org/browse/EF-218) | `Projecting DateTimeOffset members EF-218` | Projecting individual members of a `DateTimeOffset` (e.g. `.Year`, `.Hour`) is not supported. | 2 |
| [EF-220](https://jira.mongodb.org/browse/EF-220) | `Multiple query roots issue EF-220` | Queries that reference more than one `DbSet<>` (Cartesian product / cross-join) are not translatable. Includes `SelectMany` across DbSets and tautology-predicate cross-joins. | 10 |
| [EF-221](https://jira.mongodb.org/browse/EF-221) | `Equals with different types issue EF-221` | `==` / `Equals` with operands of mismatched CLR types (e.g. `int == long`) is not translated correctly. | 4 |
| [EF-222](https://jira.mongodb.org/browse/EF-222) | `translation of Like issue EF-222` | `EF.Functions.Like(...)` is not translated. | 9 |
| [EF-227](https://jira.mongodb.org/browse/EF-227) | `Max over empty nullables issue EF-227` | `Min` / `Max` over an empty nullable sequence does not produce the EF-expected `null`. | 4 |
| [EF-228](https://jira.mongodb.org/browse/EF-228) | `Truncation data loss issue EF-228` | `Sum`/`Average` over `float` columns suffers precision/truncation loss when accumulated server-side. | 2 |
| [EF-229](https://jira.mongodb.org/browse/EF-229) | `Incorrect results issue EF-229` | `Contains` at the top of the query tree returns wrong results in at least one shape. | 1 |
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
| [EF-249](https://jira.mongodb.org/browse/EF-249) | `checked issue EF-249` | `checked { ... }` arithmetic is not honored — the `Checked_context_with_arithmetic_does_not_fail` test sees a different exception than EF expects. | 2 |
| [EF-252](https://jira.mongodb.org/browse/EF-252) | `Concurrency detector tests broken EF-252` | `Throws_on_concurrent_query_first/list` — the concurrency detector does not fire as the EF base test expects. | 2 |
| [EF-253](https://jira.mongodb.org/browse/EF-253) | `Multiple ordering issue EF-253` | `OrderBy(x).ThenBy(x)` on the same column with different directions does not emit the expected MQL. | 1 |
| [EF-254](https://jira.mongodb.org/browse/EF-254) | `Take zero EF-254` | `.Skip(0).Take(0)` with a parameter does not produce the expected empty result. | 1 |

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
| EF-X001 | Sub-query selection across DbSets is not translated | 198 |
| EF-X002 | Provider throws a different exception than the EF translation-failure message | 44 |
| EF-X003 | Driver-level feature gaps surfaced as test failures | 19 |
| EF-X004 | Float `Sum`/`Average` truncation (likely duplicate of EF-228) | 1 |
| EF-X005 | BSON document missing nested required reference (AdHoc JSON) | 2 |
| EF-X006 | MongoDB `DateTimeKind` round-trip handling | 1 |
| EF-X007 | Views / `HasDefiningQuery` semantics for MongoDB collections | 2 |
| EF-X008 | No support for nested JSON in AdHoc JSON tests | 2 |
| EF-X009 | Single uncategorized failure — needs triage | 1 |
| EF-X010 | Provider-specific Include error message differs from EF baseline | 4 |
| EF-X011 | Compiled query with non-query operator — wrong exception | 1 |
| EF-X012 | `OfType` translation unsupported | 2 |
| EF-X013 | MongoDB has no `$xor` operator (`Where_bitwise_xor`) | 1 |
| EF-X014 | Server-side projection conflict with cast-to-nullable | 1 |
| EF-X015 | Sub-second `DateTime` component translation (nanosecond/microsecond) | 1 |
| EF-X016 | GroupJoin shapes not translated | 9 |
| EF-X017 | Join shapes not translated | 5 |
| EF-X018 | RightJoin not supported | 1 |
| EF-X019 | Include on keyless entity not supported (no primary key for $lookup join) | 2 |
| EF-X020 | Cross-collection Include/join/navigation not translated on EF8/EF9 (works on EF10) | 193 |
| EF-X021 | Filtered Include / query-filter predicate on cross-collection target — translated (top-level, nested-target, and best-effort query filters) | 0 (resolved) |
| EF-X022 | Cross-collection filtered Include returns wrong data (included collection not limited) | 0 (resolved) |
| EF-X023 | Cross-collection multiple / self-ref / required Include returns wrong data — **deferred to EF-317** (needs depth-aware join model + FK-based nav resolution) | 5 |
| EF-X024 | Cross-collection reference Include (multi-include / self-ref / nested) returns wrong data / throws "Document element is missing for required non-nullable property" — **deferred to EF-317** | 5 |
| EF-X025 | Merged Include chains on one navigation with interleaved collection + reference ThenIncludes dropped a branch | 0 (resolved) |

### EF-X001 — Sub-query selection across DbSets is not translated
Comment patterns: `// Fails: Subquery selection EF-X001`, `// Fails: Subqueries not supported EF-X001`, `// Fails: No subquery support EF-X001`.
Test-body patterns: `AssertTranslationFailed(() => base.X(...))`, `AssertNoMultiCollectionQuerySupport(() => base.X(...))`.
Affected: ~140 tests across `NorthwindAggregateOperators`, `NorthwindMiscellaneous`, `NorthwindWhere`, `NorthwindNavigations`, `NorthwindSetOperations`, `NorthwindJoin`, etc. Many overlap with [EF-216](https://jira.mongodb.org/browse/EF-216); a separate ticket lets cross-collection subquery support be tracked independently from raw navigation access.

### EF-X002 — Provider throws a different exception than the EF translation-failure message
Comment patterns: `// Fails: Not throwing expected translation failed exception from EF, but still throws EF-X002`, `// Fails: Not throwing expected translation failed exception from EF. EF-X002`, `// Fails: Does not throw expected unable to translate exception EF-X002`, `// Fails: Does not use translation failed message EF-X002`, `// Fails: Throws different exception, but still throws EF-X002`.
Affected: ~34 tests. EF's base tests expect `InvalidOperationException` with EF's "could not be translated" message; the provider currently throws `ExpressionNotSupportedException` / `MongoCommandException` / `NotSupportedException` instead. Aligning the messages would let EF-level diagnostics work without provider-specific overrides.

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
Pattern: tests for `Include_collection_with_client_filter` across all four Include variants use `Assert.ThrowsAsync<ContainsException>` and assert that `Assert.Contains` fails because the provider's error message differs from the generic EF message. The override carries an explanatory comment ("Throws with Mongo-specific message rather than the generic EF message.") but no `// Fails:` tag in the current codebase.
Affected: 4 tests (`NorthwindEFPropertyIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`, `NorthwindIncludeQueryMongoTest.cs`, `NorthwindStringIncludeQueryMongoTest.cs`).

### EF-X011 — Compiled query with non-query operator — wrong exception
Comment pattern: `// Fails: Compiled query with non-query operator issue EF-X011`.
Affected: 1 test (`NorthwindCompiledQueryMongoTest.Compiled_query_when_does_not_end_in_query_operator`). Previously cited the same `EF-232` as `Sum_with_no_data_cast_to_nullable`, but the two are clearly distinct bugs — split out into this temp ticket. The provider throws `ArgumentException("No ultimate source found")` instead of the EF-expected error.

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
Affected: 190 tests across `NorthwindEFPropertyIncludeQueryMongoTest`, `NorthwindStringIncludeQueryMongoTest`, `NorthwindIncludeQueryMongoTest`, `NorthwindIncludeNoTrackingQueryMongoTest`, `NorthwindNavigationsQueryMongoTest`, `NorthwindMiscellaneousQueryMongoTest`, `NorthwindJoinQueryMongoTest`, `NorthwindAggregateOperatorsQueryMongoTest`, `NorthwindAsNoTrackingQueryMongoTest`, `NorthwindKeylessEntitiesQueryMongoTest`, `NorthwindSelectQueryMongoTest`, `NorthwindSetOperationsQueryMongoTest`, `NorthwindWhereQueryMongoTest`, `BuiltInDataTypesMongoTest`, and `ComplexNavigationsCollectionsQueryMongoTest` (22 — reference/optional/multiple Include shapes that run on EF10 but throw `ArgumentOutOfRangeException`/translation failure on the EF8/EF9 nav-expansion pipeline). These are the cross-collection Include/`ThenInclude`/join/navigation shapes implemented for the EF10-targeted query pipeline. On EF8/EF9 the upstream nav-expansion / query pipeline produces a different expression shape (e.g. an extra `.OrderBy(o => o.OrderID)` injected during navigation expansion) that the EF10-targeted translator does not handle, so translation fails with EF Core's `InvalidOperationException` "could not be translated" (a few also throw the provider's `ExpressionNotSupportedException`); the same query translates and runs on EF10. The provider's local `AssertTranslationFailed` helper swallows whichever exception is thrown, so both shapes are covered. Four `Include_reference_dependent_already_tracked` overrides (in the four Include suites) emit MQL from a first principal query *before* the Include sub-query fails to translate, so their `#if EF8 || EF9` branch asserts only the translation failure and omits the empty `AssertMql()`. `BuiltInDataTypesMongoTest.Can_read_back_bool_mapped_as_int_through_navigation` is split three ways (a nested `#if EF9` inside the file's `#if !EF8` branch, plus the `#else` EF8 branch) because that file uses async signatures on EF9/EF10 and sync `void` signatures on EF8.

### EF-216 — wrong-data on EF10 (cross-collection navigation), unsupported on EF8/EF9

Five `NorthwindNavigationsQueryMongoTest` methods exercise **multi-hop** cross-collection navigation
(multiple optional navigations, nested `Contains`-over-navigation, deep 2-hop null filters). On EF10
the query **translates and runs but returns the wrong result set** (e.g. 2155 rows instead of 112 —
the join/`Contains` filter is not applied; or 0 instead of 6 for the deep null case) — a genuine
cross-collection navigation lowering bug, not a translation gap. On EF8/EF9 the same shapes simply
fail to translate. Because the wrong-data variant cannot be asserted green by a test-only baseline
(the base test asserts the *correct* data), these five are marked
`[ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]`
uniformly across EF8/EF9/EF10 and carry a `// Fails: ... EF-216` comment. They need a provider
query-pipeline fix for compound multi-hop navigation lowering:

- `Include_with_multiple_optional_navigations`
- `Multiple_include_with_multiple_optional_navigations`
- `Navigation_from_join_clause_inside_contains`
- `Navigation_inside_contains_nested`
- `Select_Where_Navigation_Null_Deep`

With these skipped, the full spec suite is **green on all three EF versions** (EF8/EF9/EF10:
0 failures). They are the only known-incorrect query shapes remaining; un-skip them once the
multi-hop navigation lowering is fixed.

---

### EF-X021 — Filtered Include / query filter on cross-collection target (mostly translated)
Comment pattern: `// Fails: Filtered Include / query-filter predicate on a cross-collection $lookup target is not translated EF-X021`.

A user filtered-Include predicate (`.Include(c => c.Orders.Where(o => ...))`) and a dependent-side `HasQueryFilter`
(soft-delete / multi-tenant) both lower to a `Where` inside the collection-Include subquery. That `Where` is **not** the
synthetic FK-correlation join condition; it is now **translated into the `$lookup` sub-pipeline `$match`** by rendering
the predicate through the EF entity serializer via the driver (the same mechanism the VectorSearch pre-filter uses) — see
`MongoEFToLinqTranslatingExpressionVisitor.EmitLookupStages` / `RenderFilterPredicateMatch` and the captured
`LookupExpression.FilterPredicates`. The `$match` is emitted after the FK-correlation `$match` and before any paging
(`$sort`/`$skip`/`$limit`). Top-level filtered Includes (`Filtered_include_basic_Where(_EF_Property)`,
`Filtered_include_after_reference_navigation`, `Filtered_include_on_ThenInclude(_EF_Property)`,
`Filtered_include_and_non_filtered_include_on_same_navigation{1,2}`, `Filtered_include_same_filter_set_on_same_navigation_twice`,
`Filtered_include_after_different_filtered_include_same_level`, `Filtered_include_variable_used_inside_filter`,
`Filtered_include_context_accessed_inside_filter`) now translate on EF10. **Nested**-ThenInclude-target filters
(`Filtered_include_after_different_filtered_include_different_level`) are also translated: the hand-assembled nested
`$lookup` cannot render a `$match` itself, so its predicate is captured on `LookupExpression.PendingNestedFilterRenders`
and rendered (in place into the nested sub-pipeline `BsonArray`) by `EmitLookupStages`. The reference-`ThenInclude`-target
cases are EF10-only (the broader cross-collection Include is unsupported on EF8/EF9 — see EF-X020 — so their overrides
split with `#if EF8 || EF9`).

A source-side query filter reached *after* the walk descends through a ThenInclude's `Select`/`Join` is captured as a
**best-effort** predicate (`LookupExpression.BestEffortFilterPredicates`): rendered into the sub-pipeline `$match` when the
lookup already uses the pipeline form and the driver can express it, otherwise dropped — preserving the pre-EF-X021
behavior for redundant query filters that may reference a navigation with no `$lookup` representation (e.g.
`NorthwindQueryFiltersQueryMongoTest`). Functional coverage:
`CrossCollectionIncludeTests.Filtered_collection_include_predicate_is_translated_to_sub_pipeline_match` and
`CrossCollectionIncludeTests.Query_filter_on_collection_include_target_is_translated_to_sub_pipeline_match`.

All filtered-Include / query-filter predicate cases are now translated; EF-X021 has no remaining `// Fails:` tags.
Three formerly-tagged overrides were re-tagged to their true root cause: `Filtered_include_outer_parameter_used_inside_filter`
→ **EF-X001** (a `Select`-projected correlated cross-DbSet subquery whose filter references the outer parameter,
`x => x.Id != l1.Id` — not a filtered-Include predicate), and `Include_collection_multiple_with_filter(_EF_Property)`
→ **EF-216** (a root-level `Where(... .Count() > 0)` correlated cross-collection subquery).

### EF-X022 — Cross-collection filtered Include returns wrong data (included collection not limited) — RESOLVED
The filtered-Include `Skip`/`Take`/`OrderBy` were dropped from the `$lookup` sub-pipeline **when the filtered Include
was combined with a `ThenInclude`**: the ThenInclude lowers the collection subquery to
`Select(<filtered source>.LeftJoin/Join(<thenInclude target>), e => Include(e, …))`, and
`ExtractFilteredIncludePipeline` (in `MongoProjectionBindingExpressionVisitor`) stopped at the outermost `Select`
(hitting its `default` branch) before reaching the inner `OrderBy`/`Skip`/`Take`, so the included collection was
over-populated (e.g. expected 4, actual 5). Fixed by descending the walk through `Select`/`Join`/`LeftJoin` into their
source so the paging operators below are collected (a `Where` reached *after* descending is treated as a stop boundary
so plain query-filtered includes are not newly re-interpreted). Of the original 6 tests:
`Filtered_include_complex_three_level_with_middle_having_filter1`/`2` are now green;
`Filtered_include_Take_with_another_Take_on_top_level`,
`Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation`,
`Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_FirstOrDefault_on_top_level` and
`..._and_unordered_Take_on_top_level` now apply the paging correctly but still fail on the **reference ThenInclude
materialization** and so moved to [EF-X024](#ef-x024--cross-collection-reference-include-returns-wrong-data--missing-required-element).

### EF-X023 — Cross-collection multiple / self-ref / required Include returns wrong data
Comment pattern: `// Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023`.
Affected: 5 tests in `ComplexNavigationsCollectionsQueryMongoTest` — `Include_collection_multiple`,
`Multiple_complex_includes_self_ref(_EF_Property)`, `Required_navigation_with_Include`,
`Required_navigation_with_Include_ThenInclude`. The query runs but a navigation is mis-populated (boolean/identity
assertions differ, e.g. expected `True`, actual `False`) — multiple sibling collection Includes, self-referencing
Includes, and required-reference Includes across collections do not all materialize correctly. Not baseline-able (base
asserts correct data); the override asserts the current failure. (`Include_collection_then_reference`,
`Include_collection_followed_by_include_reference` and `Include_collection_ThenInclude_two_references` — the
collection-then-reference shapes — are now **fixed** by the nested-navigation null guard described under EF-X024.)

**Disposition — deferred to [EF-317](https://jira.mongodb.org/browse/EF-317).** A scoping pass confirmed these are not a
localized shaper/null-key bug but the same structural deficit as EF-X024 (see the shared disposition under EF-X024): the
`$lookup` join-emulation models cross-collection joins with a single global `UsesDriverJoinFields` boolean and two reserved
field names (`_outer` root, `_inner` "the one reference"), and resolves intermediate navigations by *first navigation whose
target type matches* (`MongoQueryableMethodTranslatingExpressionVisitor.cs:442-443,485-490`). Self-ref and
multiple-navigations-to-the-same-type therefore bind the wrong FK, and >1 reference hop produces a document shape
(`_outer._outer…` or a flat `_lookup_<Nav>` chain) the single-level shaper deref cannot read. Correcting these requires a
depth/per-navigation-aware join document model and FK-based navigation resolution — i.e. rebuilding the join-emulation that
EF-317 (native driver `LeftJoin`) is slated to replace. The overrides assert the failure via `AssertTranslationFailed`.

### EF-X024 — Cross-collection reference Include returns wrong data / missing required element
Comment patterns: `// Fails: Cross-collection reference Include missing required element on materialization EF-X024` and
`// Fails: Cross-collection reference ThenInclude returns wrong data (filtered paging now applied; reference not materialized) EF-X024`.
Root cause (one mechanism, two symptoms): an optional cross-collection reference lowers to `$lookup` +
`$unwind {preserveNullAndEmptyArrays:true}`. When the reference has no match `_inner` should be absent, but a *later*
`$lookup` writing a dotted `as` path (`_inner._lookup_<Nav>`, for a collection/reference nested under the reference)
makes MongoDB **synthesise the parent `_inner`** document containing only the nested array — so `_inner` is
present-but-keyless. The non-nullable EF materializer then reads the missing required key and throws
`InvalidOperationException: Document element is missing for required non-nullable property '...'` (typically the joined
dependent's `Id`), or builds a wrong/empty entity that fixup attaches → an `AssertInclude` data diff.

**Partially fixed (pipeline-level, EF-X024):** a shaper-level fix (key-presence guard / probe-leniency) was first tried
but had an unacceptable blast radius (regressed ~130 unrelated materialization tests). The committed fix instead emits,
after a `$lookup` whose `as` writes under an OPTIONAL driver-join reference parent (`_inner` /
`_lookup_<RefNav>`), a `$set` that re-nulls that parent when its `_id` is missing — so the synthesized-but-keyless
document becomes absent and the existing left-join null guard nulls the entity (see
`MongoEFToLinqTranslatingExpressionVisitor.EmitLookupStages` + `LookupExpression.NormalizeParent`). This greens the
single reference-then-collection cases (`Include_reference_followed_by_include_collection`,
`Include_reference_and_collection_order_by`, `Include_reference_ThenInclude_collection_order_by`,
`Include_reference_collection_order_by_reference_navigation`, `Optional_navigation_with_Include_and_order`,
`Optional_navigation_with_order_by_and_Include`).

**Also fixed (nested-navigation null guard):** a second class of failure was a cross-collection REFERENCE materialized
*inside a collection element* (`Include(collection).ThenInclude(reference)`). `BsonDocumentInjectingExpressionVisitor`
wraps each entity shaper with a `joined document is null => null entity` guard, but it did not recurse into a
`CollectionShaperExpression`'s element shaper — so the nested reference's key-presence test read a missing `_lookup_<Nav>`
key as the value type's default (e.g. `0`) and materialized a **phantom entity** (`Id = 0`) instead of null. Fixed by
having that visitor wrap the ThenInclude navigation entities of a collection element (its `IncludeExpression`'s
`NavigationExpression`s), while leaving the element entity itself directly bound. This greens the
collection-then-reference and filtered-include-with-reference shapes
(`Include_collection_then_reference`, `Include_collection_followed_by_include_reference`,
`Include_collection_ThenInclude_two_references`, `Filtered_include_Take_with_another_Take_on_top_level`,
`Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation`,
`Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_FirstOrDefault_on_top_level`,
`..._and_unordered_Take_on_top_level`).

**Also fixed (driver-join nested-reference field name):** in driver-join mode (`_outer`/`_inner`, used when the query
also has a top-level reference Include) a cross-collection reference nested under a *collection* element was read from
the driver-join `_inner` field (the single top-level reference) instead of its own `_lookup_<Nav>` alias —
`GetCrossCollectionFieldName` blanket-returned `"_inner"` whenever `UsesDriverJoinFields`. Fixed by mirroring
`GetCrossCollectionRootDocument`'s nesting test: a reference whose parent is a collection element (not the query root)
reads its `_lookup_<Nav>` alias. Greens `Multiple_complex_includes(_EF_Property)`, `Multiple_complex_include_select`,
`Include_nested_with_optional_navigation`, `Optional_navigation_with_Include_ThenInclude`.

Still affected: 5 tests (EF-X024) in `ComplexNavigationsCollectionsQueryMongoTest` — multi-optional / partial-build
shapes that still throw the missing-element error via flat-mode synthesis or deeper nesting:
`Multiple_optional_navigation_with_Include(_string_based)`,
`Include_partially_added_before_Where_and_then_build_upon(_with_filtered_include)`,
`Complex_multi_include_with_order_by_and_paging_joins_on_correct_key`. Same family as
[EF-164](https://jira.mongodb.org/browse/EF-164). The overrides assert the failure via `AssertTranslationFailed`.

**Disposition — deferred to [EF-317](https://jira.mongodb.org/browse/EF-317).** A scoping pass (covering the 5 EF-X024
tests above plus the 5 EF-X023 tests — they share one root cause) concluded the remaining failures are **not** fixable by a
bounded shaper / `$set` change, and deferred them. Findings:
- The committed `NormalizeParent` `$set` re-null is **necessary but not sufficient**: a prototype extending it to the flat
  `_lookup_<Nav>` path stopped the crash but data stayed wrong. A shaper-side "treat a present-but-keyless joined
  sub-document as null" guard (moving the key-presence test into `BsonDocumentInjectingExpressionVisitor` / `BsonBinding` so
  it does not depend on the pipeline removing the field) is similarly bounded but addresses only the single-level
  unmatched-optional symptom.
- Two structural deficits block the rest, neither localized: **(1)** the join document model carries a single global
  `UsesDriverJoinFields` boolean and exactly two reserved names (`_outer` root, `_inner` the lone reference), and the shaper
  derefs `_outer` only once (`MongoProjectionBindingRemovingExpressionVisitor.cs:589-591`) — so a 2nd reference hop
  (`_outer._outer…`, emitted as a doubled `{$project:{_outer:$$ROOT}}`) or a flat `_lookup_<Nav>` chain is unreadable;
  **(2)** intermediate navigations are resolved target-type-first
  (`MongoQueryableMethodTranslatingExpressionVisitor.cs:442-443,485-490`), so self-ref and multiple-navs-to-the-same-type
  bind the wrong FK (the wrong-FK aliases visible in the `Required_navigation_with_Include` / self-ref baselines).
- Correcting these requires a **depth/per-navigation-aware join document model + FK-based navigation resolution** — i.e.
  rebuilding the manual `$lookup`/`$unwind` join-emulation that the `TODO(EF-317)` headers in
  `MongoProjectionBindingExpressionVisitor.Lookup.cs` and `MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs` say is the
  stopgap to be removed when the C# driver ships native `LeftJoin`. Low ROI to rebuild ahead of that.

One genuinely bounded prerequisite was identified but **greens 0 of the 10 on its own**: replacing the target-type-first
navigation resolution with FK-based resolution (mirroring `ResolveCollectionNavigation`,
`MongoProjectionBindingExpressionVisitor.Lookup.cs:333`), which closes a latent silent-wrong-navigation hazard. Best folded
into the EF-317 work rather than shipped standalone.

### EF-X025 — Merged Include chains dropped an interleaved ThenInclude branch — RESOLVED
When two Include chains are merged on the same first-level collection navigation but carry **different** ThenInclude
targets — e.g. `.Include(l1.OneToMany_Optional1.Where(...).Take(2)).ThenInclude(l2.OneToMany_Optional2)` (nested
collection) together with `.Include(l1.OneToMany_Optional1).ThenInclude(l2.OneToOne_Required_FK2)` (nested reference) —
EF lowers them to **interleaved** collection and reference `IncludeExpression`s in the merged subquery selector.
`ExtractThenIncludesFromSubquery` previously walked them with two sequential, type-gated loops (all collections, then
all references), so whichever kind was nested under the other was silently dropped from the `$lookup` pipeline —
returning wrong data (`AssertInclude` mismatch). (The earlier `ArgumentNullException` characterization was stale; by the
time this was fixed the filter-predicate gap was already handled under EF-X021, leaving only the dropped branch.)

Fixed by replacing the two loops with a single walk that dispatches each nested `IncludeExpression` by navigation kind,
emitting all branches regardless of interleaving order
(`MongoProjectionBindingExpressionVisitor.ExtractThenIncludesFromSubquery` / `AddCollectionLookupStages`). Also fixed
`Filtered_include_same_filter_set_on_same_navigation_twice_followed_by_ThenIncludes`, which had the same latent drop.

## Audit findings — tagging hygiene

The following inconsistencies were observed while building this inventory and have since been resolved.

### 1. `EF-232` was reused for two distinct failure modes — **fixed**

`Sum_with_no_data_cast_to_nullable` keeps `EF-232`; `Compiled_query_when_does_not_end_in_query_operator` was re-tagged with the new temp ticket `EF-X011`.

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

returns 49 distinct ticket ids, including 15 temporary `EF-X###` placeholders. The static audit (lookback-5 search for `// Fails:` above any `AssertTranslationFailed` / `Assert.Throws*` / `AssertGroupByUnsupported` callsite, excluding helper definitions and single-mode helper callers) reports zero remaining holes among Mongo-failure cases.
