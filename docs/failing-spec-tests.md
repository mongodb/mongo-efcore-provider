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
| [EF-117](https://jira.mongodb.org/browse/EF-117) | `Include issue EF-117` (each marker now carries a specific reason) | Cross-collection `Include` / `ThenInclude` is now implemented (collection, reference dependent→principal, and chained ThenInclude shapes of arbitrary depth, with tracking-mode propagation). **Most shapes now run as a single server-side `$lookup`** (collection `$lookup`; reference `$lookup`+`$unwind`; reference+collection-on-the-same-root as two `$lookup`s; reference-rooted N-level chains via nested dotted-path `$lookup`s; composite-PK-member keys via `_id.<field>`; and server-side filtered includes — ordering/paging realized as `$sort`/`$skip`/`$limit` pipeline stages). The **client-side fan-out loader is the fallback** for collection-rooted / through-collection chains, many-to-many, multi-column FKs, and user-`Where` filtered includes; the routing decision lives in `MongoIncludeCompiler.ChooseStrategy`. The originally-tagged 499 `EF-117` spec-test overrides have been resolved: those exercising the implemented shapes now `await base(async)` and assert the captured MQL (now `$lookup`-based for the server-side shapes). **Every remaining `// Fails:` marker that was generically tagged `EF-117` was individually investigated** (run each base test, capture the actual exception — all fail with EF Core's own top-level "could not be translated" `InvalidOperationException` and **no provider inner exception**, i.e. EF rejects the *surrounding* shape before Include routing runs) and **re-tagged to the ticket for its real cause**: cross-collection `Join`/`GroupJoin`/`LeftJoin`/`RightJoin` → [EF-X016](#ef-x016--cross-collection-join--groupjoin-not-translated); `from…from…`/cross-join/duplicate self-join (multiple query roots) → EF-220; correlated cross-collection sub-query (CROSS/OUTER APPLY, `let`-sub-queries) → EF-X001; `SelectMany` over a cross-document navigation, projection/`OrderBy`/query-filter that reads a cross-document navigation → EF-216; entity `GroupBy(…).Select(g => g.FirstOrDefault())` → EF-149; client method in a projection → EF-250; entity-equality predicate (`e.Manager == null`) → EF-202. **`EF-117` is now reserved for the 38 markers where lack of Include support IS the main cause** — all in `Northwind{,NoTracking,String,EFProperty}IncludeQueryMongoTest.cs` (10 distinct methods): (1) **multiple Includes from one root not yet composed** — `Include_multiple_references*`, and note the **order asymmetry** that `Include_reference_and_collection` (reference-first) works while `Include_collection_and_reference` (collection-first) does not; (2) **multi-level collection-rooted chains with inner-level paging** (`Multi_level_includes_are_applied_with_{take,skip,skip_take}` — `Customer→Orders→OrderDetails` projecting `c.Orders.ToList()`) routed to client fan-out without cross-level composition; (3) **a reference Include carried through an entity-reshaping projection** (`Include_reference_when_entity_in_projection`). Many-to-many (`ISkipNavigation`) Include throws a clean "not yet supported" error. **Known limitation — nav-referencing filtered Include:** a filtered collection Include whose USER `Where` predicate references ANOTHER navigation (e.g. `Include(c => c.Orders.Where(o => o.Customer.Name == "Alfreds"))`) now THROWS a clear `NotSupportedException` (message: "...combines a navigation-referencing predicate (from the Include filter itself or from a query filter on '\<dependent\>') with filtering, ordering, or paging...") rather than silently returning UNFILTERED results. EF expands such a predicate over a transparent-identifier element type (a `Join` into the referenced nav), which the provider can neither render server-side nor recompose onto the client-side fan-out loader. The throw lives in `MongoIncludeCompiler.ChooseStrategy` / `HasUntranslatableUserWhereInclude`; a bare MODEL query filter on the dependent (which also expands to a transparent-identifier `Where`, e.g. Northwind's `Order` filter) is left alone when NO extra include operator is present, because its `Where` sits INSIDE (before) the FK correlation `Where` while a user predicate sits OUTSIDE (after) — so a plain `.Include(c => c.Orders)` over such a filter still runs via the server-side `$lookup`. **Over-rejection (same option-3 follow-up):** when the dependent ALSO carries a navigation-referencing query filter, adding ANY filtering/ordering/paging include operator — even a plain SCALAR `Where` (`o => o.Total > 10`) or an `OrderBy` that references no navigation — pushes that query filter's transparent-identifier `Where` OUTSIDE the correlation, so it gets flagged and the include THROWS. At base this combined case was *silently wrong* (returned unfiltered data), so the throw is an improvement, but it over-rejects an otherwise-supportable shape; the message therefore names the dependent type and attributes the cause to either source so it is not misleading. **Full support ("option 3") is a candidate follow-up for BOTH the user-predicate and query-filter triggers:** translate/apply the nav-referencing predicate on the fan-out path by re-running the included `Join` + `Where` through `DbContext.Set<TDependent>()`. EF8/EF9/EF10 all now pass the full spec suite (each remaining marker is an override that asserts the current translation failure, so the suite stays green). | 38 |
| [EF-149](https://jira.mongodb.org/browse/EF-149) | `GroupBy issue EF-149` | `GroupBy` translation is severely limited; most non-trivial group-by shapes fail to translate. Includes entity `GroupBy(…).Select(g => g.FirstOrDefault())` shapes re-tagged from EF-117. | 288 |
| [EF-153](https://jira.mongodb.org/browse/EF-153) | `TagWith EF-153` | `TagWith(...)` content is silently dropped — does not appear in the emitted MQL. | 9 |
| [EF-164](https://jira.mongodb.org/browse/EF-164) | `Missing property values issue EF-164` / `Projections issue EF-164` | BSON documents that omit a required scalar (or required navigation) throw on materialization — `Project_root_with_missing_scalars`, `Project_root_entity_with_missing_required_navigation`, etc. | 3 |
| [EF-202](https://jira.mongodb.org/browse/EF-202) | `Entity equality issue EF-202` | Comparing two entities (`entity1 == entity2` / `Contains(entity)`) is not lowered to a key-equality comparison. | 8 |
| [EF-216](https://jira.mongodb.org/browse/EF-216) | `Cross-document navigation access issue EF-216` / `Navigations issue EF-216` | Navigations that cross collection boundaries cannot be translated; surfaces as `Unsupported cross-DbSet query between ...`. Documented at the helper `AssertNoMultiCollectionQuerySupport`. Now also covers cross-document-navigation shapes re-tagged from EF-117: `SelectMany` over a navigation (`c.Orders`), and projection / `OrderBy` / model-query-filter that reads a cross-document navigation member. | 318 |
| [EF-217](https://jira.mongodb.org/browse/EF-217) | `Call ToString on DateTimeOffset EF-217` | `DateTimeOffset.ToString()` cannot be translated. | 2 |
| [EF-218](https://jira.mongodb.org/browse/EF-218) | `Projecting DateTimeOffset members EF-218` | Projecting individual members of a `DateTimeOffset` (e.g. `.Year`, `.Hour`) is not supported. | 2 |
| [EF-220](https://jira.mongodb.org/browse/EF-220) | `Multiple query roots issue EF-220` | Queries that reference more than one `DbSet<>` (Cartesian product / cross-join) are not translatable. Now also covers `from…from…` / cross-join / duplicate self-join Include shapes re-tagged from EF-117 (the explicit `Join`/`GroupJoin` operator is tracked separately under EF-X016). | 42 |
| [EF-221](https://jira.mongodb.org/browse/EF-221) | `Equals with different types issue EF-221` | `==` / `Equals` with operands of mismatched CLR types (e.g. `int == long`) is not translated correctly. | 4 |
| [EF-222](https://jira.mongodb.org/browse/EF-222) | `translation of Like issue EF-222` | `EF.Functions.Like(...)` is not translated. | 9 |
| [EF-227](https://jira.mongodb.org/browse/EF-227) | `Max over empty nullables issue EF-227` | `Min` / `Max` over an empty nullable sequence does not produce the EF-expected `null`. | 4 |
| [EF-228](https://jira.mongodb.org/browse/EF-228) | `Truncation data loss issue EF-228` | `Sum`/`Average` over `float` columns suffers precision/truncation loss when accumulated server-side. | 2 |
| [EF-229](https://jira.mongodb.org/browse/EF-229) | `Incorrect results issue EF-229` | `Contains` at the top of the query tree returns wrong results in at least one shape. | 1 |
| [EF-231](https://jira.mongodb.org/browse/EF-231) | `String.FirstOrDefault issue EF-231` | `String.FirstOrDefault()` in a projection forces a client-evaluation path that is not supported. | 1 |
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
| [EF-250](https://jira.mongodb.org/browse/EF-250) | `Client eval in final projection EF-250` | `Select(...)` with a client-evaluated expression in the final projection (e.g. `i.ToString()` on a server value) is not supported. Includes an Include-with-client-method projection re-tagged from EF-117. | 11 |
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
| EF-X001 | Sub-query selection across DbSets is not translated | 190 |
| EF-X002 | Provider throws a different exception than the EF translation-failure message | 44 |
| EF-X003 | Driver-level feature gaps surfaced as test failures | 17 |
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
| EF-X016 | Cross-collection `Join` / `GroupJoin` not translated | 77 |

### EF-X001 — Sub-query selection across DbSets is not translated
Comment patterns: `// Fails: Subquery selection EF-X001`, `// Fails: Subqueries not supported EF-X001`, `// Fails: No subquery support EF-X001`.
Test-body patterns: `AssertTranslationFailed(() => base.X(...))`, `AssertNoMultiCollectionQuerySupport(() => base.X(...))`.
Also covers correlated cross-collection sub-queries re-tagged from EF-117: CROSS / OUTER APPLY Include shapes (`Include_collection_with_{cross,outer}_apply_with_filter[_non_equality]`), `let`-bound correlated sub-queries (`Include_in_let_followed_by_FirstOrDefault`, `Tags_on_subquery`), and the join-suite `*_correlated_subquery_take` / `Take_in_collection_projection_with_FirstOrDefault_on_top_level` tests. Comment pattern for those: `// Fails: Subquery selection EF-X001 — …`.
Affected: ~190 tests across `NorthwindAggregateOperators`, `NorthwindMiscellaneous`, `NorthwindWhere`, `NorthwindNavigations`, `NorthwindSetOperations`, `NorthwindJoin`, the Include suites, etc. Many overlap with [EF-216](https://jira.mongodb.org/browse/EF-216); a separate ticket lets cross-collection subquery support be tracked independently from raw navigation access.

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

### EF-X016 — Cross-collection `Join` / `GroupJoin` not translated
Comment pattern: `// Fails: cross-collection <Join|GroupJoin|LeftJoin|RightJoin|left join …> not translated … EF-X016.`
Affected: 77 tests. The bulk is the `NorthwindJoinQueryMongoTest.cs` conformance suite (the `LeftJoin`/`RightJoin`/`Join_*`/`GroupJoin_*` overrides, 54) plus two `NorthwindMiscellaneousQueryMongoTest.cs` join tests, the four `Northwind*IncludeQueryMongoTest.cs` join-clause Include variants (`Include_collection_with_{join,left_join,right_join}_clause_with_filter`, `Include_collection_on_join_clause_with_order_by_and_filter`, `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`), and the keyless-view left join (`NorthwindKeylessEntitiesQueryMongoTest.Entity_mapped_to_view_on_right_side_of_join`, cf. EF-X007). These were previously tagged `Include (joins) issue EF-117`; the explicit LINQ `Join` / `GroupJoin` operator (including `LeftJoin`/`RightJoin`, `DefaultIfEmpty` left joins, joins against a local collection, and tautology joins) is a distinct unsupported feature, not an Include gap — `MongoQueryableMethodTranslatingExpressionVisitor` rejects join/group-join shapes early. Distinct from EF-220 (uncorrelated `from…from…`/cross-join multiple roots), EF-X001 (correlated cross-collection sub-query / APPLY), and EF-216 (`SelectMany` over a navigation).

---

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

returns 51 distinct ticket ids. Sixteen temporary `EF-X###` placeholders are defined (EF-X001–EF-X016); 15 appear as `// Fails:` markers (EF-X010 uses a `ContainsException` assertion rather than a `// Fails:` tag). The static audit (lookback-5 search for `// Fails:` above any `AssertTranslationFailed` / `Assert.Throws*` / `AssertGroupByUnsupported` callsite, excluding helper definitions and single-mode helper callers) reports zero remaining holes among Mongo-failure cases.
