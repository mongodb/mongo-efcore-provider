# EF8/EF9 LeftJoin-Shim Native-Include Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Include(referenceNavigation)` go through the native MQL translator (instead of falling back to driver-LINQ) under EF8 and EF9, matching EF10's existing behavior, by teaching the native join-candidate recognizer about EF Core's internal `LeftJoin` shim.

**Architecture:** `NativeSlotPopulator.PopulateNativeSlots` records a "candidate reference-Include join" only when it sees `QueryableMethods.Join`, `QueryableMethods.GroupJoin`, or (EF10-only) the public `QueryableMethods.LeftJoin`. Under EF8/EF9, EF Core lowers `Include` on an *optional* reference navigation to its own private `LeftJoin` shim method instead of the public one, so the candidate is never recorded and the query is marked non-native before the join-scope binder (`NativeJoinScopeProjectionBinder`) ever runs. Everything downstream of that recording step — `TranslateJoinCore`, join-scope confirmation, the `VisitMethodCall` dispatch table — is already method-identity-agnostic, so the fix is a single added branch at the recording site, no wider changes.

**Tech Stack:** C#, EF Core (multi-version: EF8/EF9/EF10 via build configuration), MongoDB C# driver, xUnit, TestContainers (`mongodb/mongodb-atlas-local`).

**Spec:** No separate spec document — the design was established conversationally in the originating session (see the "Phase 1" summary in that conversation). This plan is self-contained: the root cause, fix, and verification strategy are documented in the Architecture section above and in each task below.

## Global Constraints

- Preserve file BOMs on every file you edit.
- `src/` is `<Nullable>enable</Nullable>` — no new nullability warnings.
- `<NoWarn>EF1001</NoWarn>` is already set; internal EF Core APIs are fair game.
- Unit and functional tests use plain xUnit `Assert.*` — no FluentAssertions.
- Tests run serially (`DisableTestParallelization = true`) — don't add parallelism.
- Multi-EF targeting is via build **configuration**, not target framework: `Debug EF8`, `Debug EF9`, `Debug EF10`. `#if EF8`, `#if EF9`, `#if EF8 || EF9`, `#if !EF8 && !EF9` are the guards in play here.
- Commit messages and the eventual PR title start with `EF-322: `.
- Functional tests need Docker; run with `MONGODB_URI` and `ATLAS_URI` unset so `TestServer` boots an isolated `mongodb/mongodb-atlas-local` container per test process.
- Never commit with `--no-verify` or skip hooks.

## Review Focus

- **A confirmed EF8/EF9 candidate join must still decline correctly when no `Include` follows it** (a plain user `.Join`/`.GroupJoin` with no navigation): the candidate/confirm counting in `MongoSelectDefinition` must still gate on confirmation, not just candidacy, or a user's ordinary join could be silently mis-translated. Covered by the existing (version-agnostic) `User_join_is_not_admitted_by_the_candidate_join_signal` test in `NativeReferenceIncludeTests.cs` — Task 1 must confirm this still passes on EF8/EF9 after the fix.
- **Required vs. optional reference navigations must keep their distinct unwind semantics** (`preserveNullAndEmptyArrays: false` vs. `true`) once EF8/EF9 goes native — a wrong flag here silently drops or keeps the wrong rows. Covered by `Required_reference_Include_goes_native_with_an_inner_unwind` (already cross-version) and the newly-ungated `Optional_reference_Include_goes_native_with_a_left_outer_unwind` (Task 1).
- **Multi-level reference-Include chains (2+ navigations deep) must keep declining to fallback**, not silently produce wrong results, once EF8/EF9's single-level case goes native — the depth-1-only confirmation-count guard in `MongoQueryableMethodTranslatingExpressionVisitor` is by design, not incidental, and must not accidentally widen. Covered by rerunning the full `NorthwindIncludeQueryMongoTest` `NativeOnly` sweep in Task 4 and diffing against the known Phase-2-scoped 5-method failure set.
- **Collection-Include-over-join and other non-reference-Include join shapes must be unaffected** — this fix only touches the *candidate-recording* condition, shared by every native join, so a regression here could silently change collection-Include or plain user-join behavior on EF8/EF9. Covered by the broader `Query` suite `NativeOnly` sweep (not just Include tests) in Task 4.
- **Baseline drift across the whole Include/Join spec-test surface**, not just `NorthwindIncludeQueryMongoTest` — other spec classes (`NorthwindStringIncludeQueryMongoTest`, `NorthwindIncludeNoTrackingQueryMongoTest`, `NorthwindEFPropertyIncludeQueryMongoTest`, `NorthwindJoinQueryMongoTest`, `NorthwindMiscellaneousQueryMongoTest`) may have their own `#if EF8 || EF9` MQL branches driven by the same fallback. Covered by the sweep in Task 3.

---

### Task 1: Fix the native candidate-join recognizer and prove it with the existing optional-reference-Include test

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:355-361`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs:973-991` (remove the `#if !EF8 && !EF9` / `#endif` guard around `Optional_reference_Include_goes_native_with_a_left_outer_unwind` so the test runs on every EF version)

**Interfaces:**
- Consumes: `MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(MethodInfo method)` (already public/internal-static, defined at `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:108-110`) — takes the raw (possibly closed-generic) `MethodInfo` and normalizes internally, so pass `call.Method`, not `methodDefinition`.
- Produces: no new public surface. `NativeSlotPopulator.PopulateNativeSlots` now also calls `mongoQ.Select.MarkSawCandidateReferenceIncludeJoin()` when the method is the EF8/EF9 `LeftJoin` shim, under `#if EF8 || EF9`.

- [ ] **Step 1: Remove the version guard on the regression test so it runs (and fails) on EF8/EF9**

Edit `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`. Find:

```csharp
#if !EF8 && !EF9
    [Fact]
    public void Optional_reference_Include_goes_native_with_a_left_outer_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Optional_reference_Include_goes_native_with_a_left_outer_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Carrier).ToList();

        // Left-outer: rows with no FK and rows with a DANGLING FK both survive, navigation null. All 4
        // seeded orders survive.
        Assert.Equal(4, results.Count);
        Assert.Contains(results, o => o.Carrier == null);

        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }");
    }
#endif
```

Replace with the same body, minus the `#if`/`#endif`:

```csharp
    [Fact]
    public void Optional_reference_Include_goes_native_with_a_left_outer_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Optional_reference_Include_goes_native_with_a_left_outer_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Carrier).ToList();

        // Left-outer: rows with no FK and rows with a DANGLING FK both survive, navigation null. All 4
        // seeded orders survive.
        Assert.Equal(4, results.Count);
        Assert.Contains(results, o => o.Carrier == null);

        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }");
    }
```

- [ ] **Step 2: Build EF9 and run the test to confirm it fails without the source fix**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF9" --no-build \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests.Optional_reference_Include_goes_native_with_a_left_outer_unwind"
```

Expected: **FAIL** with `MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException` (the query declines to native and `MongoQueryMode.NativeOnly` forbids the fallback). Run with `MONGODB_URI` and `ATLAS_URI` unset so Docker provides the test server.

- [ ] **Step 3: Apply the source fix**

Edit `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`. Find:

```csharp
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#endif
                )
        {
            // Might be EF's nav-expansion of a single-level reference Include. Record a candidate rather
            // than marking non-native; TranslateSelect confirms it when the trailing IncludeExpression
            // matches the recognizer. Unconfirmed candidates route to Fallback, so this is default-deny and
            // a user join is unaffected. See MongoSelectDefinition §Reference-Include candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
```

Replace with:

```csharp
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#else
                 // EF8/EF9 lower a GroupJoin+DefaultIfEmpty pair — including EF's own nav-expansion of an
                 // OPTIONAL reference Include — onto this same private shim rather than a public LeftJoin
                 // method (see Ef8Ef9LeftJoinMethod's remarks). Recognize it here exactly like the EF10
                 // public LeftJoin above, or every EF8/EF9 optional reference Include is marked non-native
                 // before the join-scope binder ever runs (EF-322).
                 || MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method)
#endif
                )
        {
            // Might be EF's nav-expansion of a single-level reference Include. Record a candidate rather
            // than marking non-native; TranslateSelect confirms it when the trailing IncludeExpression
            // matches the recognizer. Unconfirmed candidates route to Fallback, so this is default-deny and
            // a user join is unaffected. See MongoSelectDefinition §Reference-Include candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
```

`MongoQueryableMethodTranslatingExpressionVisitor` lives in `MongoDB.EntityFrameworkCore.Query.Visitors`, a different namespace than `NativeSlotPopulator` (`MongoDB.EntityFrameworkCore.Query.NativeTranslation`). Add the using directive at the top of the file (alongside the existing `using MongoDB.EntityFrameworkCore.Query.Expressions;`):

```csharp
using MongoDB.EntityFrameworkCore.Query.Visitors;
```

- [ ] **Step 4: Rebuild and confirm the test passes on EF9, then verify EF8 and EF10 too**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF9" --no-build \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```

Expected: all tests in the class **PASS**, including `Optional_reference_Include_goes_native_with_a_left_outer_unwind`.

Repeat for EF8:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF8" --no-build \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```

Expected: all tests **PASS**.

Then confirm no regression on EF10 (the branch this fix doesn't change, since `#if !EF8 && !EF9` still takes the original path there):

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```

Expected: all tests **PASS** (unchanged from before this task).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-322: recognize the EF8/EF9 LeftJoin shim as a candidate reference-Include join"
```

---

### Task 2: Attempt to un-gate the two dependent reducer/null-predicate reference-Include tests

**Files:**
- Modify (tentative): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs:1035-1103` (the `#if !EF8 && !EF9` block containing `Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly` and `Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate`)

**Interfaces:**
- Consumes: nothing new — this task only removes/keeps an `#if` guard depending on measured outcome.
- Produces: nothing new. This task's outcome is binary (un-gate and commit, or leave as-is) — it does not block Task 3 or Task 4 either way.

These two tests exercise `Include(o => o.Carrier).First(o => o.Carrier == null)` under `MongoQueryMode.Native` (not `NativeOnly`) — a shape whose *own* predicate correctly declines to native and must fall back correctly. The existing code comment (`ReattachComposedOperator`'s fix) describes a *separate*, already-fixed bug in the driver-LINQ rewrite path unrelated to the candidate-join recording this plan changes. It is not yet known whether that fix is version-agnostic or itself EF10-scoped, so this task measures it directly rather than assuming.

- [ ] **Step 1: Temporarily remove the guard and run under EF9**

Edit the file to remove the `#if !EF8 && !EF9` / `#endif` pair around both methods (same mechanical edit style as Task 1 Step 1 — delete the two directive lines, keep the method bodies unchanged).

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF9" --no-build \
  --filter "FullyQualifiedName~Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly|FullyQualifiedName~Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate"
```

(Rebuild first if Task 1's build is stale: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"`.)

- [ ] **Step 2: Branch on the result**

If both tests **PASS** on EF9: rebuild and run the same filter under `-c "Debug EF8"` too. If they pass there as well, keep the guard removed (the tests now run on all three EF versions) and go to Step 3a.

If either test **FAILS** on EF9 or EF8: put the `#if !EF8 && !EF9` / `#endif` guard back exactly as it was (`git checkout -- tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs` to discard this task's edit), and go to Step 3b instead — do not spend further time debugging this separate fallback-path issue; it is out of scope for this plan.

- [ ] **Step 3a (tests passed on all versions): Commit the un-gate**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-322: run the reference-Include reducer/null-predicate fallback tests on EF8/EF9 too"
```

- [ ] **Step 3b (tests failed on EF8/EF9): Leave the guard in place, no commit needed**

Confirm `git status` shows no changes to this file (the `git checkout --` in Step 2 already reverted it). Note in your final report to the plan's reviewer that this specific fallback-path shape remains EF10-only and is a known follow-up, not blocking this plan.

---

### Task 3: Regenerate MQL baselines across the Include/Join spec-test surface

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs`
- Modify (as needed, see Step 1): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`, `NorthwindEFPropertyIncludeQueryMongoTest.cs`, `NorthwindJoinQueryMongoTest.cs`, `NorthwindMiscellaneousQueryMongoTest.cs`

**Interfaces:**
- Consumes: `EF_TEST_REWRITE_BASELINES=1` env var (documented in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`) — when set, a failing `AssertMql(...)` call rewrites its own baseline in place from captured MQL, and the test still reports failed (that's the "a rewrite happened" signal).
- Produces: updated `AssertMql(...)` baseline strings in the affected test files, and (where EF8/EF9 output now matches EF10's exactly) removal of the now-redundant `#if EF8 || EF9 ... #else ... #endif` conditional around the assertion, collapsing to a single unconditional `AssertMql(...)` call.

This task is inherently generative — the new native MQL text cannot be hand-authored correctly without running the translator, so every baseline change in this task must come from `EF_TEST_REWRITE_BASELINES=1`, never typed by hand.

- [ ] **Step 1: Build EF9 and find the current NativeOnly failure set for the Include spec classes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF9" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest|FullyQualifiedName~NorthwindJoinQueryMongoTest|FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/ef9_include_after_fix.txt 2>&1
grep "^  Failed" /tmp/ef9_include_after_fix.txt | grep -oE "[A-Za-z0-9_]+Test\.[A-Za-z0-9_]+" | sort -u
```

Compare this list against the known Phase-2-scoped shared gaps (`Include_reference_when_entity_in_projection`, `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`, `Include_with_complex_projection_does_not_change_ordering_of_projection`, `Repro9735`, `Include_collection_with_multiple_conditional_order_by`) plus `Include_property`/`Include_property_after_navigation`/`Include_property_expression_invalid`/`Then_include_property_expression_invalid`/`Include_specified_on_non_entity_not_supported` (these assert `AssertMql()` with no arguments — i.e., they expect an exception before any MQL is logged — and are unaffected by this fix either way). Any name in the grep output **not** in that expected set is new information: read that test's current override in its source file before Step 2 to understand what still needs fixing, and if it looks unrelated to the LeftJoin-shim gap, stop and flag it to your reviewer rather than guessing at a fix — it's outside this plan's diagnosed root cause.

- [ ] **Step 2: Regenerate baselines for the classes with real fallback-driven MQL differences**

For each spec class from Step 1 that still shows failures beyond the expected Phase-2 set, or that has visibly different `#if EF8 || EF9 ... #else ... #endif` MQL branches in its source, run a scoped rewrite:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF9" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest"
```

The run reports failed — that is the expected rewrite signal, not a problem. Immediately diff what changed:

```bash
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs
```

For each changed test method, check whether the diff makes the `#if EF8 || EF9` branch's `AssertMql(...)` string byte-for-byte identical to the `#else` (EF10) branch's string. If so, collapse the `#if`/`#else`/`#endif` into a single unconditional `AssertMql(...)` call using the (now-shared) MQL text, and delete the redundant duplicate. If the two branches still differ after regeneration, leave both branches in place — that's a genuine remaining version difference, not a leftover from this fix.

Also check for now-obsolete `// Failed: ...` comments (the transitional marker for not-yet-rebaselined Include work) on any test this task fixes — remove them, per the spec-test convention that `// Failed:` markers are removed once fixed and never added new. Leave `// Fails: ...EF-XXXX` comments alone (those mark durable, ticketed gaps unrelated to this fix).

Repeat the rewrite + diff + collapse-or-keep process for each of the other classes named in Task 3's file list that showed affected output in Step 1.

- [ ] **Step 3: Rebuild and rerun without the rewrite flag to confirm green**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF9" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest|FullyQualifiedName~NorthwindJoinQueryMongoTest|FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest"
```

Expected: **all PASS** (normal mode, fallback allowed — this proves the new baselines are correct, independent of whether the query went native).

- [ ] **Step 4: Repeat Steps 1-3 for EF8**

Same commands, `-c "Debug EF8"` throughout. EF8 and EF9 share the exact same shim and candidate-recording code path, so expect the same set of affected tests and the same baseline outcomes; if EF8's results diverge from EF9's for any test, treat that as new information to flag rather than assume it's the same fix.

- [ ] **Step 5: Confirm EF10 is untouched**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest|FullyQualifiedName~NorthwindJoinQueryMongoTest|FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest"
```

Expected: **all PASS**, with zero diff to any `#else`/unconditional EF10 baseline text (only the EF8/EF9 side of any collapsed conditional should have changed in Step 2). Run `git diff --stat` over the same file list and confirm no line inside an `#if !EF8 && !EF9` or EF10-only region changed.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindEFPropertyIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindJoinQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs
git commit -m "EF-322: regenerate EF8/EF9 Include/Join MQL baselines now that reference-Include goes native"
```

(Only `git add` the files that actually changed in Steps 2-4; skip any from the list above with no diff.)

---

### Task 4: Full regression sweep and sign-off

**Files:** none modified — this task only runs tests. If it surfaces a regression, stop and report it rather than attempting an ad hoc fix; the specific remediation depends on what's found.

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: the evidence that Phase 1 is complete and safe to hand off.

- [ ] **Step 1: NativeOnly sweep over the full Query suite for EF9, both spec and functional tests**

This is broader than Include — the fix touches shared join-candidate-recording code used by every native `Join`/`GroupJoin`/`LeftJoin`.

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF9" --no-build --filter "FullyQualifiedName~Query" --logger "console;verbosity=normal" > /tmp/ef9_spec_query_nativeonly.txt 2>&1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF9" --no-build --filter "FullyQualifiedName~Query" --logger "console;verbosity=normal" > /tmp/ef9_functional_query_nativeonly.txt 2>&1
tail -8 /tmp/ef9_spec_query_nativeonly.txt
tail -8 /tmp/ef9_functional_query_nativeonly.txt
```

Expected: the spec-test failure list contains only the known Phase-2-scoped shapes (the 5 shared cross-version gaps, doubled for `async: true/false`, plus the `Include_property*`/`Include_specified_on_non_entity_not_supported` no-MQL-expected tests) — no new failures introduced by this fix, and no previously-passing test now failing. The functional-test run should show **zero** `NativeTranslationNotSupportedException` failures outside tests that explicitly `Assert.Throws` for one (i.e., any unexpected failure here is a regression — stop and report it, don't patch around it).

- [ ] **Step 2: Repeat Step 1 for EF8**

Same commands with `-c "Debug EF8"`. Compare the failure list against EF9's — they should match, since both share the identical shim and code path.

- [ ] **Step 3: Full three-version regular (non-NativeOnly) test-all run**

```bash
/test-all
```

Expected: green across EF8, EF9, and EF10 for the full solution (unit, functional, and specification test projects) — this is the final proof that nothing outside the Query/Include surface regressed (Storage, Metadata, Serializers, etc. are all exercised by the full suite even though this fix never touches their code).

- [ ] **Step 4: Final report**

Summarize, for your human partner or the branch reviewer: the EF9 and EF8 `NativeOnly` failure counts before this plan (74/235 baseline, measured pre-fix) versus after (expected: matching EF10's ~10/237, i.e. the same 5 distinct methods), which spec-test files got baseline updates, and the outcome of Task 2's stretch attempt (un-gated or left gated, and why). No commit for this task — it's verification only.
