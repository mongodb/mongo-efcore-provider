# Tests that worked on `main` but are refused, weakened, changed or deleted on EF-322c

Baseline: local `main` @ 1d7ec56a, compared with EF-322c HEAD c7854e7f (`git diff main...HEAD`). Method: no test files
were deleted. I compared every changed test file under FunctionalTests, UnitTests and SpecificationTests method by
method (scripts are in this directory: cmp.py, nat.py, nat2.py, newfail.py, nobase.py) and then read each hit by hand.

- **UnitTests:** no removed lines anywhere. Every change is an addition, so nothing was weakened.
- **FunctionalTests:** 13 files have removed lines; each one was read.
- **SpecificationTests:**
  - No branch-only overrides were added.
  - No override that passed on main now asserts failure, except the ones listed in the table below.
  - 140 of the 146 methods with `IsNativeOnly` branches already asserted failure on main. The other 6 are
    Random_next_1..6.
  - I hand-checked the 15 `#if`-split cases. All of them failed on main in every EF version.

## Main table

| # | Test (branch file:line) / main path:line | What main asserted | What the branch asserts | Category | Was main's result correct? | Source of change | Recommendation |
|---|---|---|---|---|---|---|---|
| 1 | FunctionalTests/Query/ProjectionTests.cs:696 `Count_with_value_converter_in_predicate_is_refused`; main ProjectionTests.cs:673 `Count_with_value_converter_in_predicate` | `Count(p => p.orderFromSun > 4L) == 4` (long with `HasConversion<int>()`) | `Assert.Throws<NotSupportedException>` in every mode. Only `== 4L` is still checked. | REFUSED-NOW (+renamed) | **Yes for this query.** The driver serialized 4L through the converter to `{$gt:4}`. It is wrong only for a constant outside int range: EF's CastingConverter is unchecked, so `> 3_000_000_000L` wraps to a negative number and matches every row. | c7854e7f, EF-337 | **Owner ruling.** Suggest making it work natively with a range-aware constant: translate when the constant is in range; when it is out of range, fold the comparison to a constant true/false or refuse. A blanket refusal also blocks the correct in-range case. |
| 2 | ProjectionTests.cs:587 `Select_projection_alias_with_bson_representation_uses_source_property_serializer`; main :569 | `OrderBy(p => p.orderFromSun)` (int as `BsonType.String`) → Positions 1,2 | Query changed to `OrderBy(p => p.name)`, which happens to give the same order (Mercury, Venus) | QUERY-CHANGED (dodges EF-337 sort refusal) | **Correct only because of the data.** The server sorts the strings lexically. With the seed "1","2" that matches numeric order, but "10" < "9" would sort wrongly. | c7854e7f, EF-337 | **Owner ruling.** By the letter of the goal this test did produce correct results on main. A correct native form is possible: sort on `$convert`/`$toInt` of the stored string for numeric-as-string representations. Otherwise keep the refusal and record main as silently wrong. Either way, restore the original `orderFromSun` ordering in the test once decided. |
| 3 | ProjectionTests.cs:603 `..._ef_property_uses_source_property_serializer`; main :584 | same (EF.Property projection) | same change | QUERY-CHANGED | same as #2 | c7854e7f, EF-337 | same as #2 |
| 4 | ProjectionTests.cs:619 `..._widening_cast`; main :599 | same (`(long)` cast) | same change | QUERY-CHANGED | same as #2 | c7854e7f, EF-337 | same as #2 |
| 5 | ProjectionTests.cs:635 `..._nullable_lift`; main :614 | same (`(int?)` cast) | same change | QUERY-CHANGED | same as #2 | c7854e7f, EF-337 | same as #2 |
| 6 | SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs:4643/4657/4672/4687 `Random_next_is_not_funcletized_1..4` (EF8/EF9 only); main :4534/4548/4563/4578 | `try { base } catch { Assert.Fail }`, so the base `AssertQuery` (results equal to LINQ-to-objects) had to pass. **The earlier audit missed these four.** Its "Exception is expected" comment is misleading: the test passed only when the query worked. | `Assert.ThrowsAsync<InvalidOperationException>` (NativeOnly: AssertNativeTranslationFailed) | REFUSED-NOW | **Yes, by coincidence.** Random was evaluated once rather than per row, but each predicate gives the same rows for any value in range: (1) `< Next()-int.Max` is always empty, (2) `> Next(5)` returns all rows, (3) `> Next(0,10)` returns all rows, (4) `-20000 > new Random(15).Next()` is empty. | c7854e7f, EF-255 | **Owner ruling.** A native per-row translation exists: `Next(max)` → `$floor($multiply([$rand, max]))`, `Next(min,max)` → `min + floor(rand*(max-min))`, `Next()` → `floor(rand*int.MaxValue)`. That is correct per-row semantics (`$rand` needs server 4.4.2+), and all six spec tests would pass. A seeded `new Random(15)` sequence can't be reproduced, but no test depends on it. |
| 7 | same file :4702/4717 `Random_next_is_not_funcletized_5/_6` (EF8/EF9); main :4593/4603 | `await base` + AssertMql `$gt: 2` / `$gt: 5` (a constant baked in) | Throws IOE / native-failed | REFUSED-NOW | **Yes, by coincidence.** `OrderID > new Random(15).Next(5)` returns all rows for any value. | c7854e7f, EF-255 | same as #6 |
| 8 | SpecificationTests/Query/NorthwindFunctionsQueryMongoTest.cs:1575 `Where_guid_newguid` (EF8/EF9 arm only; EF10 still calls base and passes); main :1719 | `await base` (`Where(c => Guid.NewGuid() != default)` returns all customers) | EF8/EF9: `Assert.ThrowsAsync<InvalidOperationException>` | REFUSED-NOW | **Yes.** NewGuid() is never `default`, so evaluating it once is harmless here. | c7854e7f, EF-255 | **Owner ruling.** Options: fold `NewGuid() ==/!= Guid.Empty/default` to a constant natively, or accept the refusal. Aggregation has no per-row UUID generator, so a projected `NewGuid()` stays refused. EF10 already passes, presumably because EF10 parameterizes NewGuid. |
| 9 | FunctionalTests/Query/OwnedEntityTests.cs:698/716/735/752 `OwnedEntity_{non_,}nullable_collection_is_empty_when_{null,missing}` ×3 tracking modes; main :693/711/729/745 `..._is_null_when_...` | A missing or BSON-null owned array materializes as `null` | Materializes as an empty collection | EXPECTATION-CHANGED (renamed) | **Debatable.** EF's contract for a collection navigation is empty, not null. Main's result depended on the CLR field initializer. | 016638c0 "Working" squash, **EF-358** (not a native ticket; BREAKING entry added in 7b1f7b6e) | Keep the change. This is a deliberate non-native behaviour change (ruled under EF-358), not a native parity gap. Just confirm the owner accepts it as a break. |
| 10 | OwnedEntityTests.cs ~:1422 `OwnedEntity_collection_can_be_tested_for_null` and ~:1444 `OwnedEntity_collection_field_can_be_tested_for_null` | Row matched by `children == null` equals an entity with `children` null | The same row now equals an entity with `children = []` (the predicate still matches) | EXPECTATION-CHANGED | Same as #9 | 016638c0, EF-358 | Same as #9 |
| 11 | FunctionalTests/Compatibility/StoredDataStillReadableTests.cs:338 `_nullableDefault` fixture (used by the read tests) | Stored provider-8.1 doc `"OwnedMany": null` reads back as null | Reads back as `[]`. A write-only fixture was added so null is still written. | EXPECTATION-CHANGED | Same as #9 | 016638c0, EF-358 | Same as #9 |
| 12 | FunctionalTests/Storage/IndexTests.cs:971 `Query_does_not_throw_when_multiple_vector_indexes_but_one_specified` (Atlas-gated) | Seeds entities with required (NRT) `string Filter1` unset (persisted as BSON null), then VectorSearch returns 2 rows | Seeds `Filter1 = ""` so the materializer doesn't reject the null | EXPECTATION-CHANGED (data changed) | **No.** The data is invalid. Main's BsonBinding.GetPropertyValue also throws "Document element is null for required non-nullable property". Either main's path didn't reach it or the Atlas test didn't run on main; I didn't verify which. | 54b72293, EF-343 | Keep. This is not a working query that was lost. |

### Changes reviewed and dismissed: main also threw or failed

- **ProjectionTests.cs:763 `Select_projection_nested_calculated_with_value_converter_throws` and :1123
  `Select_projection_mixed_server_client_not_supported` (ece55cf7).** The tests swapped `ToUpper()` for `.Length` and
  `Trim()`. Main asserted a throw for both. Reading the branch code, the ORIGINAL ToUpper queries most likely
  **succeed with correct results** now:
  - `ProjectionAnalyzer.HasCaseMappingProjectedValue` (which recurses into a nested `new {}`) keeps a projection with a
    projected ToUpper off driver push-down.
  - `MongoProjectionBindingExpressionVisitor` binds ToUpper client-side over the receiver.
  - So `orderFromSun * 2` and `FormatLabel(...)` are evaluated on the client.

  The edit preserved the throw the tests are about. This is an **improvement over main, not a regression**, and could be
  pinned with positive tests. I have not verified it by running.
- **ProjectionTests `Select_projection_group_by_not_supported`** became `Select_projection_group_by` with real results.
  Main threw; the branch works. Improvement.
- **CrossCollectionRelationshipTests:** four `*_declines_rather_than_returning_wrong_rows` tests are now
  `*_returns_the_correct_page/rows` with row-identity asserts. Improvement.
- **RequiredNavigationUnwindTests:** `Optional_reference_Include_is_not_translated_on_EF8_EF9` was deleted. Main threw;
  the EF10-only tests now run on all versions. Improvement.
- **CrossCollectionInclude, SameTargetTypeJoin and NavigationlessJoinChain:** only comment or `#if` removals, plus
  tests widened to more EF versions.
- **UnsupportedQueryTests `GroupBy_cannot_be_translated` / `_with_element_selector_`:** the message asserts were relaxed
  to type only. It still throws, as on main. n/a.
- **FirstSingleTests:** the message strings gained a trailing period (EF's message instead of the driver's). It throws on
  both. n/a.
- **WhereTests:** the MQL baseline changed (`$literal`). The emitted MQL is not contract. LoggingTests is unrelated to
  query results.
- **Spec `SelectMany_after_client_method` and `Client_OrderBy_GroupBy_Group_ordering_works`:** the upstream base is
  `AssertTranslationFailed` in both EF8 and EF10. Main threw, and the branch throws a different type. Legitimate
  (re-verified).
- **Spec BuiltInDataTypes, AdHocJson, MongoCompliance (Math/String bases now implemented) and MongoAssert:** these are
  flips from fails to passes, or accept a different exception for a case that already failed on main.

## BREAKING-CHANGES.md: queries that now throw or change result (diff main...HEAD)

| Entry | Main behaviour | Was main correct? | Recommendation | Tests pinning it |
|---|---|---|---|---|
| EF-337: ordering, relational comparisons and aggregates over a value-converted or string-represented property throw `NotSupportedException` | The server compared, sorted or aggregated the **stored** form | **No in general** (string sorting, `$sum` ignoring strings, wrapping narrowing constants). **Yes for order-preserving cases**: single-digit strings, order-preserving custom converters for comparisons, in-range constants on a narrowing converter, enum names in value order. | Keep the refusal for genuinely non-order-preserving forms; main was wrong and should be recorded for a fix on main. **Owner ruling** on the two shapes where a correct native form is feasible: (a) numeric-as-string sort/compare via `$convert`; (b) narrowing-converter relational comparisons with in-range constants (ProjectionTests #1). Note the entry refuses every custom converter, even provably order-preserving ones. | ProjectionTests #1–#5; RepresentedPropertyPushdownTests.cs, NativeComputedSortTests.cs (new) |
| EF-459: date parts and Add{Years,Months,Days} over a `HasDateTimeKind(Local)` property throw | Computed on the stored UTC instant | **No**, except on a UTC host (DST and day boundaries also break). | Keep the refusal; main was wrong. No main test regressed: none of the main tests using Local kind changed. | NativeDateTimeKindReadbackTests.cs (new) |
| EF-255: `Random.Next` / `Guid.NewGuid` in a query throw | Evaluated once at translation, so every row saw the same value | **No in general** (OrderBy Random doesn't shuffle; a projected Random is the same on every row). **Yes by coincidence** for the 7 spec tests above. | **Owner ruling.** `Random.Next` has a correct per-row native form via `$rand`. For NewGuid, keep the refusal except for comparisons against a constant Empty/default. | Spec #6–#8; NonDeterministicFunctionTests.cs (new) |
| EF-217: DateTimeOffset.ToString / string concatenation (code: MongoEFToLinqTranslatingExpressionVisitor.cs:795-800, :1239-1247; client-eval at MongoProjectionBindingExpressionVisitor.cs:340) | The driver rendered the `{DateTime,Ticks,Offset}` sub-document as BSON JSON text | **No**, it returned garbage. | Keep: a top-level projection is now evaluated client-side and correctly; other positions throw. **Gap:** there is no BREAKING-CHANGES.md entry for this. It is arguably not a break because main was wrong, but the concat refusal is new loud behaviour. | DateTimeOffsetMemberProjectionTests.cs (new) |
| Projecting a required property whose element is absent or null now throws (7b1f7b6e) | Projection returned the CLR default (0/false/null). Whole-entity reads already threw. | **Debatable.** The two read paths disagreed on main. | **Owner ruling** (this is review finding F10). DriverLinq does not restore it. | NativeArrayProjectionTests etc. (new); no main test was edited for it apart from IndexTests (#12) |
| Missing or null embedded array materializes empty (EF-358) | `null`, depending on the field initializer | Debatable: EF's contract is empty | Keep (non-native ruling) | #9–#11 |
| Numeric cast in `Where` | The driver silently dropped the cast; an overflow now raises a server error | **No** | Keep (native fixes it) | NativeCastTests.cs |
| Missing field `== null` in projection/order is now true | `false` for a missing field (inconsistent with `Where`) | **No** (it disagreed with LINQ-to-objects and with `Where`) | Keep | — |
| ToUpper/ToLower in a projection applied client-side (all modes) | `$toUpper`: ASCII-only, null → "" | **No** for non-ASCII and null | Keep (more correct) | NativeStringCaseMappingTests.cs |
| `Equals(..., OrdinalIgnoreCase)` is Unicode-aware; `\z` anchor | `$strcasecmp` is ASCII-only; `^...$` matched a trailing `\n` | **No** for non-ASCII and trailing newline | Keep | NativeStringCaseInsensitiveMatchTests.cs |

Note: local `main` (1d7ec56a) lacks several upstream merges that are on `origin/main` and on the "Working" squash 016638c0
(EF-221, EF-254, EF-295, EF-376, EF-379, EF-380, driver 3.11.2). Rows #9–#11 come from that non-native squash, not from
native work.
