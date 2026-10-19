# Shape 3 code-path investigation findings (2026-09-27)

Pure investigation spike (Task 1 of the reference-Include-materialization plan). Temporary
`Console.WriteLine` probes were added at five sites — `TranslateSelect`'s two recognizer/confirm
arms, `TryGetReferenceIncludeChain`, `TryConfirmReferenceIncludeChain`,
`IsSingleEligibleNativeJoinScope`, `BindResultMember` (after its unwrap loop), and
`NativeJoinScopeProjectionBinder.TryBindProjection` — run against real spec/functional tests, then
fully reverted. A sixth, follow-up probe (`TryGetMixedReferenceAndCollectionIncludeChain`'s own entry
and outcome) was added and reverted in a second pass, after initial review flagged that this doc had
inferred that recognizer's non-participation from the absence of other output rather than showing its
own resolved outcome directly — see the "Follow-up probe" callouts under each target test below. No
production code changed; this file is the only diff in the commit(s) that add it.

Environment: EF10, `Debug EF10` configuration, TestContainers-provisioned MongoDB (no
`MONGODB_URI`/`ATLAS_URI` set), build succeeded with 0 errors both before adding probes and after
reverting them.

## Baseline failure mode (Step 3, unmodified tree)

```
Include_reference_when_entity_in_projection(async: True) [FAIL]
  System.FormatException : Element '_id' does not match any field or property of class
  Microsoft.EntityFrameworkCore.TestModels.Northwind.Order.
    at MongoDB.Bson.Serialization.BsonClassMapSerializer`1.DeserializeClass(...)
    ...
    at MongoDB.EntityFrameworkCore.Storage.BsonBinding.GetElementValue[T](...)
    at lambda_method401(Closure, QueryContext, BsonDocument)

Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(async: False) [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: 2
  Actual:   0
    at Microsoft.EntityFrameworkCore.TestUtilities.QueryAsserter.AssertIncludeCollection[TElement](...)
```

Correction to the plan's assumption: `Include_reference_when_entity_in_projection` does **not** fail
with silent null/wrong data in this tree — it fails LOUDLY with a `FormatException` during BSON
deserialization. This is expected and intentional: commit `0b0a7a6d` ("restore loud failure for a
reference-Include in a join-scope leaf") deliberately restored this exact `FormatException` as the
correct behavior for the unsupported shape, and its own commit message states "Verified against the
real Northwind spec test: `Include_reference_when_entity_in_projection` now fails with
`FormatException` again (same shape as before this plan's work)". So Task 1's job is not to explain a
mystery silent-data bug for this test — it's to confirm which recognizer arm this exception path goes
through, which the probes below do.

`Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` DOES fail with
silent wrong data as the design doc expected: an empty collection (`Actual: 0`) instead of 2 rows.

## Probe output: `Include_reference_when_entity_in_projection` (Step 5)

```
[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized=False
[SHAPE3-PROBE] IsSingleEligibleNativeJoinScope: JoinScope.Levels.Count=1, Joins.Count=1
[SHAPE3-PROBE] NativeJoinScopeProjectionBinder.TryBindProjection: CALLED
[SHAPE3-PROBE] BindResultMember: alias=o, includeWrappers.Count=1, allCollectionIncludes=False
[SHAPE3-PROBE] BindResultMember: alias=CustomerID, includeWrappers.Count=0, allCollectionIncludes=True
```
(async:True and async:False runs produced byte-identical probe sequences.)

**Follow-up probe (added after initial review): does `TryGetMixedReferenceAndCollectionIncludeChain`
actually get evaluated and decline, or is it merely never reached?** `TranslateSelect` is an
`if (TryGetReferenceIncludeChain...) ... else if (TryGetMixedReferenceAndCollectionIncludeChain...) ...`
chain (lines 417/427), so the mixed recognizer IS evaluated whenever the pure one returns false — this
must be shown directly, not inferred from the absence of a `TranslateSelect: ...CONFIRMED/DECLINED
(mixed chain arm)` line. A temporary probe was re-added at the top of
`TryGetMixedReferenceAndCollectionIncludeChain` itself and both target tests re-run (with only this one
probe active, isolated from the five-probe run above); the temporary probe was reverted immediately
after capturing this output (`git diff` on both visitor files confirmed empty before and after). Actual
captured output for `Include_reference_when_entity_in_projection` (both async:True/False identical):

```
[SHAPE3-PROBE] TryGetMixedReferenceAndCollectionIncludeChain: CALLED
[SHAPE3-PROBE] TryGetMixedReferenceAndCollectionIncludeChain: recognized=False
```

This directly confirms `TryGetMixedReferenceAndCollectionIncludeChain` IS called (as the `else if`
control flow guarantees) and returns `false` — the selector's single reference-Include (`o.Customer`)
has no collection-Include sibling, so `TryWalkIncludeChain`'s `collectionLevel == null` check declines
it. It is evaluated-and-declined, not skipped/never-entered as an earlier draft of this doc
mischaracterized it. Substantively this changes nothing about the conclusion (the mixed-chain arm still
never CONFIRMS and `TranslateSelect` still falls through to the join-scope arms below), but the claim is
now backed by a quoted probe line rather than inferred from silence.

Instead, EF Core's nav-expansion has already lowered this
Include-in-projection into an internal join-scope construct (no explicit `.Join()` in the user's LINQ),
which is why `IsSingleEligibleNativeJoinScope` fires and reports a 1-level join scope
(`JoinScope.Levels.Count=1`, `Joins.Count=1`) — `NativeJoinScopeProjectionBinder.TryBindProjection` is
then called and, per its own logic, calls back into `BindResultMember` per projection member. The `o`
member (the whole `Order` entity, wrapped by the `Customer` reference-Include) unwraps to
`includeWrappers.Count=1`, `allCollectionIncludes=False` — since the wrapped nav is a reference (not
collection), `BindResultMember`'s fast rebind path (gated on `allCollectionIncludes`) is skipped by
design (commit `0b0a7a6d`), falling through to `TryGetDocumentConstructionProjection` / ultimately
`BindSelectManyMember`, which reads the leaf under an alias the native `$project` never emits under —
this is exactly what produces the `FormatException` (deserializing the wrong document shape as
`Order`).

**Conclusion for this test: Hypothesis A** — it reaches `IsSingleEligibleNativeJoinScope` /
`NativeJoinScopeProjectionBinder.TryBindProjection` / `BindResultMember`'s join-scope machinery, and
never reaches `TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain`'s separate arm at all.

## Probe output: `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` (Step 5)

```
[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized=False
[SHAPE3-PROBE] IsSingleEligibleNativeJoinScope: JoinScope.Levels.Count=1, Joins.Count=1
[SHAPE3-PROBE] NativeJoinScopeProjectionBinder.TryBindProjection: CALLED
[SHAPE3-PROBE] BindResultMember: alias=cust, includeWrappers.Count=0, allCollectionIncludes=True
[SHAPE3-PROBE] BindResultMember: alias=order, includeWrappers.Count=1, allCollectionIncludes=True
```
(async:False and async:True runs produced identical probe sequences.)

**Same follow-up probe, run against this test.** Given the test's name and shape (Include on the
"right side" of a left join — a genuine mixed reference+collection Include, `Customer` (reference) plus
`Orders` (collection) — this was the test most likely to actually reach
`TryGetMixedReferenceAndCollectionIncludeChain` with a `true` result, so it needed the same direct check
rather than an inference. Actual captured output (both async variants identical):

```
[SHAPE3-PROBE] TryGetMixedReferenceAndCollectionIncludeChain: CALLED
[SHAPE3-PROBE] TryGetMixedReferenceAndCollectionIncludeChain: recognized=False
```

So even for this test — whose LINQ shape looks the most "mixed" of the two — the recognizer is called
(per the `else if` control flow) and still returns `false`. The reason is structural, not incidental:
`TryGetMixedReferenceAndCollectionIncludeChain` requires the reference and collection Includes to be
recognized together as ONE selector's Include-chain shape (`TryWalkIncludeChain` over the lambda passed
to `TranslateSelect`'s own `.Select(...)`), but this test's actual LINQ
(`Include(o => o.Customer).ThenInclude...`-equivalent Northwind base-test shape reaching this provider
as a join-scope-recognized internal construct with `new { cust, order }` as separate join-scope leaves,
not as a single `IncludeExpression` chain in one selector) never presents `TryGetReferenceIncludeChain`/
`TryGetMixedReferenceAndCollectionIncludeChain` with a chain to walk in the first place — both
recognizers operate over `selector` (`TranslateSelect`'s own lambda parameter), and by the time
`TranslateSelect` runs for this query, EF's nav-expansion has already rewritten the Include information
into the join-scope's own `IncludeExpression`-wrapped leaves (visible instead in `BindResultMember`'s
`alias=order, includeWrappers.Count=1` line above), not into a chain hanging off this `selector` body.

This test is the MIXED reference+collection shape the brief flagged as a possibility, and it IS
different from the first test in one concrete respect: `TryGetReferenceIncludeChain` still returns
`recognized=False` (so, same as the first test, `TranslateSelect`'s pure-reference and mixed-chain
arms are both called-and-declined, confirmed directly above rather than assumed from their absence, and
this again reaches the join-scope machinery), but at `BindResultMember` the
`order` alias (the collection-Include leaf, `Orders` under `Customer`) unwraps with
`allCollectionIncludes=True` — i.e., the wrapped nav here IS a collection, so `BindResultMember`'s fast
rebind path is taken and returns cleanly (no exception). The AssertMql in the spec test file confirms
the expected shape is a `$lookup`/`$unwind` join (Customers → Orders) followed by a further nested
`$lookup` for `OrderDetails`. So the collection-Include materialization itself does NOT throw here —
it silently produces zero collection rows instead, matching the observed `Expected: 2, Actual: 0`
baseline failure. The bug for this test is NOT the reference-Include arm question at all; it lives
elsewhere in the join/`$lookup` mechanics (the "outer identifier … on right side of left join" name
strongly suggests the join's localField/foreignField resolution picks the wrong side when the
Include'd collection sits on the right/inner side of a LeftJoin-shaped join scope), a separate,
already-known class of bug from this same design doc's problem space, not something this task's probes
were built to diagnose further.

**Conclusion for this test: Hypothesis A also** (reaches the join-scope arms, never
`TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain`'s separate arm) — but the actual failure
mechanism differs from the first test: no exception is thrown for THIS test's `BindResultMember` call
(the collection-Include path succeeds structurally), and the silent wrong data (empty collection)
originates downstream of the arms this task instrumented.

## Probe output: the two already-passing bare reference-Include tests (Step 6)

`Optional_reference_Include_goes_native_with_a_left_outer_unwind` and
`Required_reference_Include_goes_native_with_an_inner_unwind` (both PASSED):

```
[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized=True, referenceLevels.Count=1
[SHAPE3-PROBE] TranslateSelect: TryGetReferenceIncludeChain recognized, chain.Count=1
[SHAPE3-PROBE] TryConfirmReferenceIncludeChain: chain.Count=1, transitiveLevels.Count=0
[SHAPE3-PROBE] TranslateSelect: TryConfirmReferenceIncludeChain CONFIRMED (pure reference chain arm)
[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized=True, referenceLevels.Count=1
[SHAPE3-PROBE] TranslateSelect: TryGetReferenceIncludeChain recognized, chain.Count=1
[SHAPE3-PROBE] TryConfirmReferenceIncludeChain: chain.Count=1, transitiveLevels.Count=0
[SHAPE3-PROBE] TranslateSelect: TryConfirmReferenceIncludeChain CONFIRMED (pure reference chain arm)
```

Note the complete ABSENCE of any `IsSingleEligibleNativeJoinScope`, `TryBindProjection`, or
`BindResultMember` probe lines for these two tests — they never touch the join-scope machinery at all.

**These two working tests do NOT share any arm with the two failing target tests.** They go
exclusively through `TryGetReferenceIncludeChain` (recognized=True) → `TryConfirmReferenceIncludeChain`
(CONFIRMED) in `TranslateSelect`'s pure-reference-chain branch (line ~417-425), and never reach
`IsSingleEligibleNativeJoinScope`/`NativeJoinScopeProjectionBinder`/`BindResultMember` at all. This is a
structurally clean separation: the two target failing tests reach the join-scope arms because EF
Core's nav-expansion inserted an internal join for their shape (an Include composed alongside another
projected member, `new { o, o.CustomerID }` / `new { cust, order }`), whereas these two working tests'
selector shape (a bare `.Include(...).Select(x => x)`-equivalent, no sibling projected member forcing a
join) recognizes as a pure Include chain with no join at all. **A Task 3 fix targeting the join-scope
arms (`IsSingleEligibleNativeJoinScope`/`TryBindProjection`/`BindResultMember`) cannot regress these two
tests structurally, since they never execute that code path** — though Task 3 should still re-run them
as a regression check, since a shared helper (e.g. `TryWalkIncludeChain`, used by both
`TryGetReferenceIncludeChain` and the mixed-chain recognizer) could in principle be touched
incidentally.

## Probe output: Shape 4 control repro (Step 7)

Rather than adding a new throwaway `[Fact]`, the exact repro
(`db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })` under
`MongoQueryMode.NativeOnly`, `JoinTestDbContext`/`SeedOwnersAndOrders()`) already exists verbatim as
`NativeJoinTests.Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`
(added by commit `0b0a7a6d` for exactly this purpose) — so this existing test was run with probes
active instead of adding a new file, avoiding any untracked scratch file to clean up:

```
[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized=False
[SHAPE3-PROBE] IsSingleEligibleNativeJoinScope: JoinScope.Levels.Count=1, Joins.Count=1
[SHAPE3-PROBE] NativeJoinScopeProjectionBinder.TryBindProjection: CALLED
[SHAPE3-PROBE] BindResultMember: alias=o, includeWrappers.Count=1, allCollectionIncludes=False
[SHAPE3-PROBE] BindResultMember: alias=OwnerId, includeWrappers.Count=0, allCollectionIncludes=True
```
Test result: **Passed** (the test's own assertion accepts either "throws" or "succeeds with Owner
populated" — the probe sequence being byte-for-byte the same shape as
`Include_reference_when_entity_in_projection`'s, i.e. `o` unwraps to `includeWrappers.Count=1,
allCollectionIncludes=False`, confirms it takes the identical decline-to-`BindSelectManyMember` path,
so it throws rather than silently succeeding — consistent with the test passing via its `threw` branch,
not its `Assert.NotNull(Owner)` branch).

Shape 4 (`Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })`, no explicit `.Join()`) hits
the **join-scope arms** (`IsSingleEligibleNativeJoinScope`/`TryBindProjection`/`BindResultMember`), NOT
the pure `TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain` arm — confirming that EF Core's
nav-expansion inserts an internal join-scope construct for this simpler shape too, exactly as it does
for `Include_reference_when_entity_in_projection`. This is the SAME code path as that first target
test (not merely an analogous one) — same recognizer sequence, same `allCollectionIncludes=False`
decline, same downstream `BindSelectManyMember` fallback.

## Recommendation

**All three of the reference-Include-through-a-join-scope shapes examined here — the
`Include_reference_when_entity_in_projection` spec test, its functional-test twin
(`Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`), and
the mixed-shape `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`
spec test — confirm Hypothesis A: they all reach `IsSingleEligibleNativeJoinScope` →
`NativeJoinScopeProjectionBinder.TryBindProjection` → `BindResultMember`, and NONE of them ever reach
`TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain`'s separate arm** (that arm's own
`recognized=False` fires immediately for all three, because EF Core's nav-expansion has already turned
each of these shapes into an internal join-scope construct before `TranslateSelect` ever sees them).
Task 3 should therefore execute **Branch 3A** (the join-scope-side fix — teaching
`BindResultMember`/`NativeJoinScopeProjectionBinder` to materialize a reference-Include leaf instead of
declining it, mirroring the existing collection-Include rebind path but registering a reference lookup
instead), not Branch 3B (which would target the separate `TryGetReferenceIncludeChain` arm — proven
here to be structurally unreachable for either target test). The two already-passing bare
reference-Include tests are provably isolated from this arm (Step 6 findings above) and can be used as
a pure regression check without expecting them to exercise the fix at all. One caveat worth carrying
into Task 3: the two target tests, despite sharing the same recognizer/arm sequence, currently fail via
two DIFFERENT downstream mechanisms — one via a hard exception (the reference-Include leaf, `o`,
declining to `BindSelectManyMember`) and one via silently-empty data (the collection-Include leaf,
`order`, succeeding structurally but returning zero rows for a reason outside `BindResultMember`
entirely, likely a `$lookup` localField/foreignField mismatch specific to Include-on-the-right-side-of-
a-LeftJoin). A Branch-3A fix aimed only at the reference-Include decline may fix the first test without
touching the second's root cause at all — Task 3 should treat these as two separate bugs sharing one
recognizer path, not one bug with two symptoms.
