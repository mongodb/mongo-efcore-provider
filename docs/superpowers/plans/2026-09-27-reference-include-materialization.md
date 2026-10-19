# Reference Include Materialization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make reference Includes (`Order.Owner`, EF Core's `Include_reference_when_entity_in_projection` /
`Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` shapes) actually
materialize correct data under the native query pipeline, instead of the current state (declines loudly for
one confirmed shape; silently returns null/empty for the two real spec tests).

**Architecture:** Task 1 is a pure investigation spike — no production code — that empirically determines
which of two competing hypotheses explains the two real spec-test failures, using temporary diagnostic
logging plus the existing `SpyLoggerProvider`/`MongoEventId.ExecutedMqlQuery` technique. Task 2 is
independent of that finding: it closes a known, narrower symmetry gap (the join-scope *staging* side has the
same unguarded `IncludeExpression` unwrap the *materialization* side already got a guard for in commit
`0b0a7a6d`). Tasks 3+ are gated on Task 1's finding — this plan states both branches concretely; the
executor follows the one Task 1's write-up selects and explicitly skips the other, noting so in that task's
own commit message.

**Tech Stack:** C#, xUnit (`Assert.*`, no FluentAssertions), EF Core provider internals
(`MongoQueryableMethodTranslatingExpressionVisitor`, `MongoProjectionBindingExpressionVisitor`,
`NativeJoinScopeProjectionBinder`), MongoDB C# driver, `TestServer`/`TemporaryDatabaseFixture` for
integration tests, EF Core's own `NorthwindIncludeQueryTestBase` (spec-conformance base class) for the two
target regression tests.

**Spec:** `docs/superpowers/specs/2026-09-25-reference-include-materialization-design.md`

## Global Constraints

- Task 1 is a **pure investigation spike** — no production-code changes, no committed diagnostic logging.
  Its only committed artifact is a written findings doc.
- Do not re-litigate root cause A or A2 (collection-Include recognition/materialization, both committed) —
  only the symmetry check called out in the design doc's investigation step 4 (this plan's Task 2) touches
  that area again.
- The two already-passing tests (`Optional_reference_Include_goes_native_with_a_left_outer_unwind`,
  `Required_reference_Include_goes_native_with_an_inner_unwind` in `NativeReferenceIncludeTests.cs`) must
  stay passing through every task.
- The ultimate acceptance criteria are the two real EF Core spec tests — `Include_reference_when_entity_in_projection`
  and `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` — passing with
  **correct data**, across all 4 Northwind sibling classes (`NorthwindIncludeQueryMongoTest`,
  `NorthwindStringIncludeQueryMongoTest`, `NorthwindEFPropertyIncludeQueryMongoTest`,
  `NorthwindIncludeNoTrackingQueryMongoTest`), in the **default** query mode (native is the default on this
  branch), not just under `MONGODB_EF_NATIVE_ONLY=1`.
- A reference Include mixed with a collection Include in the same chain
  (`TryGetMixedReferenceAndCollectionIncludeChain`) is a distinct case from a pure reference chain
  (`TryGetReferenceIncludeChain`) — a fix for one must not be assumed to cover the other without a test.
- Follow this repo's multi-EF-version discipline: run any test that must hold across EF8/EF9/EF10 build
  configurations (`Debug EF8`/`Debug EF9`/`Debug EF10`) if the fix touches version-conditional code; none of
  the files this plan touches currently have `#if EF8`/`#if EF9`/`#if !EF8` guards, so no new guard is
  expected unless Task 1's or Task 3's investigation finds one is needed.

## Review Focus

- **A `ThenInclude` chain** (reference→reference or reference→collection on the same target) is untested by
  anything in this plan's own new tests unless Task 3's fix touches shared Include-handling code — if it
  does, add a `ThenInclude` test even though no existing test exercises this shape (design doc's own Review
  Focus).
- **The mixed reference+collection Include chain** (`Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`
  is exactly this shape) must not be assumed fixed by whatever fixes the pure-reference case — it has its own
  recognizer (`TryGetMixedReferenceAndCollectionIncludeChain`) and needs its own passing assertion.
- **Shape 4** (`db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })`, no join at all) is grouped
  with Shape 3 in the design doc as "likely the same bug, isolated" — a fix must not be declared complete
  without also checking this no-join shape, not just the two spec tests that happen to involve an internal
  join.
- **Task 2's guard must not turn a currently-working shape into a decline.** Symmetry with `0b0a7a6d`'s
  `BindResultMember` guard is only safe if the staging side's unwrap is provably unreached by any *currently
  passing* collection-Include-on-an-Inner-leaf test — verify with the existing native join-scope test suite,
  not by inspection alone.
  before landing the guard.
- **Silent wrong data must never reappear as a regression exit condition.** Every new/modified test in this
  plan that can observe a null/empty navigation must assert either a throw or a fully-populated navigation —
  never accept "empty but no exception" as a passing outcome, mirroring
  `Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`'s own
  assertion style.
- **A fix that reaches `TryConfirmReferenceIncludeChain` or `BindResultMember`'s shared code must not regress
  the working bare reference-Include tests.** Both existing passing tests and the two real spec tests must be
  run together in Task 3's own verification step, not just the spec tests in isolation.

---

### Task 1: Investigate Shape 3's actual code path (pure spike, no production code)

**Files:**
- Create: `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md` (the task's only committed
  artifact)
- Temporarily edit, then revert before commit (never commit these edits):
  - `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
    (`TranslateSelect` ~line 417-433, `TryGetReferenceIncludeChain` ~line 1522-1548,
    `TryConfirmReferenceIncludeChain` ~line 1771, `IsSingleEligibleNativeJoinScope` ~line 1017,
    `BindResultMember` ~line 3390-3430)
  - `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs`
    (`TryBindProjection` ~line 630 onward)

**Interfaces:**
- Consumes: nothing from a prior task (this is the first task).
- Produces: a written, dated, on-disk determination of "Hypothesis A" vs. "Hypothesis B" vs. "neither" for
  Shape 3, which Task 3 reads and branches on. Also produces (as a side observation, written into the same
  doc) whatever it learns about Shape 4 and the two already-passing bare reference-Include tests' code path,
  since Task 3's Review Focus depends on knowing whether they share code with Shape 3.

- [ ] **Step 1: Confirm the environment is ready**

```bash
docker info >/dev/null 2>&1 || open -a Docker
```

If Docker was just started, wait for `docker info` to succeed before continuing (it took about 20s the last
time this repo's tests needed it).

- [ ] **Step 2: Build EF10 debug**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

Expected: build succeeds (0 errors).

- [ ] **Step 3: Reproduce the CURRENT failing behavior of both target spec tests, unmodified**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_reference_when_entity_in_projection|FullyQualifiedName~NorthwindIncludeQueryMongoTest.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join"
```

Record the exact failure mode (assertion diff showing `null` vs. `"FOLKO"`, or an empty vs. populated
collection, or an exception — write down which). This is the baseline the rest of this task's logging must
explain.

- [ ] **Step 4: Add temporary `Console.WriteLine` probes**

At the top of each of these five methods/arms, add one line identifying which arm fired and with what
resolved outcome (chain recognized? confirmed? declined? — read the existing local variable right after the
call and print it):

```csharp
Console.WriteLine($"[SHAPE3-PROBE] TryGetReferenceIncludeChain: recognized={referenceIncludeChain != null}");
```

(one line per site, adapted to that site's own locals — e.g. at `TryConfirmReferenceIncludeChain`'s entry
print the incoming `chain.Count`/`transitiveLevels.Count`, at `IsSingleEligibleNativeJoinScope`'s entry print
`mongoQueryExpression.Select.JoinScope?.Levels.Count`, at `BindResultMember`'s entry print
`allCollectionIncludes` right after the unwrap loop completes, at `NativeJoinScopeProjectionBinder.TryBindProjection`'s
entry print whether it was even called for this query).

- [ ] **Step 5: Rebuild and re-run the two target spec tests with probes active**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_reference_when_entity_in_projection|FullyQualifiedName~NorthwindIncludeQueryMongoTest.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join" \
  -- xunit.parallelizeAssembly=false
```

Capture stdout (the `[SHAPE3-PROBE]` lines) alongside the assertion failure. This tells you, per test: which
arm(s) fired, in what order, and what each returned.

- [ ] **Step 6: Run the two already-passing bare reference-Include tests with the SAME probes active**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Optional_reference_Include_goes_native_with_a_left_outer_unwind|FullyQualifiedName~Required_reference_Include_goes_native_with_an_inner_unwind"
```

Capture the same probe output. Compare against Step 5's output — do these working tests hit the SAME arms,
or different ones? This directly answers the design doc's Review Focus question about whether a Task 3 fix
risks the working path.

- [ ] **Step 7: Run the Shape 4 control repro with the same probes active**

Add a throwaway `[Fact]` (not committed) to a scratch test file, or run interactively, exercising:

```csharp
db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })
```

against the `JoinTestDbContext`/`SeedOwnersAndOrders()` fixture already used by
`NativeJoinTests.cs` (same context helpers this file's other tests use), under
`MongoQueryMode.NativeOnly`. Capture probe output.

- [ ] **Step 8: Remove every probe added in Step 4 and Step 7's throwaway fact**

```bash
git diff --stat  # confirm only the two visitor files show diffs, and the throwaway fact file if any was added as a new untracked file
git checkout -- src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
                 src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs
```

Delete the throwaway scratch fact file if one was created. Rebuild once more to confirm the tree is back to
its pre-Task-1 state:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
git status  # must show no diff in the two visitor files
```

- [ ] **Step 9: Write the findings doc**

Create `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md` containing:
- Which hypothesis (A, B, or neither) the probe output supports for
  `Include_reference_when_entity_in_projection`, with the literal probe output quoted.
- The same for `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` — note
  explicitly if it takes a DIFFERENT path than the first test (it is the MIXED reference+collection shape,
  so it may hit `TryGetMixedReferenceAndCollectionIncludeChain` instead of `TryGetReferenceIncludeChain`, and
  that difference must be recorded, not assumed away).
- Whether the two already-passing bare reference-Include tests share any of the same arms — if the answer is
  yes, name exactly which ones, since Task 3 must not touch those without re-verifying them.
- Whether Shape 4's control repro hits `TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain` (a pure
  reference chain, no join) or the join-scope arms (unexpected, would mean EF's nav-expansion inserts an
  internal join even for this simpler shape too).
- A one-paragraph recommendation for which of Task 3's two branches (3A/3B) to execute, or a third option if
  the evidence supports neither hypothesis as written.

- [ ] **Step 10: Commit the findings doc only**

```bash
git add docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md
git commit -m "$(cat <<'EOF'
EF-322: record Shape 3 code-path investigation findings

Pure investigation spike (design doc's mandated Task 1) — determines whether
Include_reference_when_entity_in_projection and
Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join
reach the join-scope BindResultMember/TryConfirmReferenceIncludeChain machinery
(Hypothesis A) or the separate TryGetReferenceIncludeChain/TryConfirmReferenceIncludeChain
arm (Hypothesis B), via temporary diagnostic logging (removed before this commit —
diff is the findings doc only). No production code changes.
EOF
)"
git status  # confirm only the findings doc is staged/committed
```

---

### Task 2: Verify and, if needed, fix Shape 2 (Inner-leaf staging/materialization symmetry)

Independent of Task 1's finding — this closes a narrower, already-understood gap: the join-scope *staging*
side (`NativeJoinScopeProjectionBinder.cs`) has the same unguarded `IncludeExpression` unwrap that the
*materialization* side (`BindResultMember`) already got a guard for in commit `0b0a7a6d`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs:231-235`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 (independent).
- Produces: a proven answer to "is Shape 2 reachable, and if so does it now decline cleanly or crash/silently
  wrong-data" — Task 3 does not depend on this, but the design doc's Review Focus item about not touching the
  root-cause-A2 area without verification applies here.

- [ ] **Step 1: Write a test reproducing Shape 2**

Add to `NativeJoinTests.cs` (mirroring `Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`'s
own "throws OR correct, never silently wrong" assertion style):

```csharp
[Fact]
public void Reference_included_Inner_leaf_of_a_join_scope_never_returns_silently_wrong_data()
{
    // Design doc Shape 2 (UNVERIFIED, PARKED at A2's final review): NativeJoinScopeProjectionBinder's own
    // staging-side unwrap (this file's own remarks on the Inner-leaf recognition arm) has the SAME unguarded
    // IncludeExpression unwrap that BindResultMember (the materialization side) already got a guard for in
    // commit 0b0a7a6d. If this Inner leaf gets staged as native while BindResultMember declines it, the two
    // halves disagree. This test proves which of "clean decline" / "correct" / "silently wrong" actually
    // happens today.
    var seed = SeedOwnersAndOrders();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Reference_included_Inner_leaf_of_a_join_scope_never_returns_silently_wrong_data));

    static List<(string OwnerName, Order Order)> Run(JoinTestDbContext db) =>
        db.Owners.Join(db.Orders.Include(r => r.Owner), o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r })
            .AsEnumerable().Select(x => (x.Name, x.r)).ToList();

    List<(string OwnerName, Order Order)>? results = null;
    var threw = false;
    try
    {
        results = Run(db);
    }
    catch
    {
        threw = true;
    }

    // Mirrors Shape 1's own test contract: EITHER a clean decline (throws) OR fully correct data — silently
    // null/empty on the Inner leaf's OWN Include (r.Owner) must never happen.
    if (!threw)
    {
        Assert.NotEmpty(results!);
        Assert.All(results!, x => Assert.NotNull(x.Order.Owner));
    }
}
```

- [ ] **Step 2: Run it against the CURRENT (pre-fix) code to observe today's behavior**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Reference_included_Inner_leaf_of_a_join_scope_never_returns_silently_wrong_data"
```

Record the outcome. If it PASSES today (either a clean throw, or genuinely correct data), the gap the design
doc flagged as "may exist" does not manifest for this shape — skip Step 3, note in the commit message that
Shape 2 was verified unreachable/already-safe, and commit just the test.

If it FAILS with `Assert.NotNull` failing (i.e., `Order.Owner` came back null with no exception — silently
wrong data), proceed to Step 3.

- [ ] **Step 3: If Step 2 failed, apply the symmetric guard**

In `NativeJoinScopeProjectionBinder.cs`, mirror `0b0a7a6d`'s `BindResultMember` guard. Change:

```csharp
var unwrappedLeafBody = leafBody;
while (unwrappedLeafBody is IncludeExpression include)
{
    unwrappedLeafBody = include.EntityExpression;
}
```

to:

```csharp
var unwrappedLeafBody = leafBody;
var allCollectionIncludesInLeaf = true;
while (unwrappedLeafBody is IncludeExpression include)
{
    if (include.Navigation is not { IsCollection: true })
    {
        allCollectionIncludesInLeaf = false;
    }

    unwrappedLeafBody = include.EntityExpression;
}
```

Then gate the recognition that follows (the `MongoTransparentScopeResolver.TryResolveScopeDepth(unwrappedLeafBody, ...)`
call a few lines below) so a `false` `allCollectionIncludesInLeaf` falls through to decline this leaf, exactly
as `BindResultMember`'s own guard falls through to its pre-existing fallback — do not stage a
`MongoProjection` for a leaf containing any reference Include.

- [ ] **Step 4: Re-run the test to confirm it now passes (as a clean decline)**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Reference_included_Inner_leaf_of_a_join_scope_never_returns_silently_wrong_data"
```

Expected: PASS.

- [ ] **Step 5: Run the full native join-scope and reference-Include suites to confirm no regression**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeJoinTests|FullyQualifiedName~NativeReferenceIncludeTests|FullyQualifiedName~NativeJoinScopeProjectionBinderTests"
```

Expected: all PASS, including the two already-passing bare reference-Include tests.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs
git commit -m "$(cat <<'EOF'
EF-322: guard NativeJoinScopeProjectionBinder's Inner-leaf unwrap against reference Includes

Design doc Shape 2 (parked, unverified at A2's final review): the staging side's
IncludeExpression unwrap had no IsCollection check, mirroring the gap
BindResultMember (the materialization side) already got a guard for in 0b0a7a6d.
[Verified unreachable today — no code change needed beyond the pinning test. /
Verified reachable and silently wrong — same allCollectionIncludes-style guard
applied here, symmetric with 0b0a7a6d.]
EOF
)"
```

(Pick the bracketed sentence matching what Step 2 actually found; delete the other.)

---

### Task 3: Fix Shape 3 (branches on Task 1's finding — execute exactly one of 3A/3B)

Read `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md` (Task 1's output) before starting.
Execute **3A** if it names Hypothesis A, **3B** if it names Hypothesis B. If it names neither, stop and write
a new short design note describing what it found instead — do not force the evidence into one of these two
branches.

**Files (both branches touch these; each branch's steps say which):**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (3A)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  (both branches, different regions: `BindResultMember` for 3A, `TryConfirmReferenceIncludeChain`'s
  post-confirmation fold for 3B)
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs` and its
  3 siblings (`NorthwindStringIncludeQueryMongoTest.cs`, `NorthwindEFPropertyIncludeQueryMongoTest.cs`,
  `NorthwindIncludeNoTrackingQueryMongoTest.cs`) — baselines regenerate, do not hand-write `AssertMql` bodies
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (promote Shape 1's spike
  to a real passing test once it stops declining)

**Interfaces:**
- Consumes: `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md` (Task 1).
- Produces: `Include_reference_when_entity_in_projection` and
  `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` passing with correct
  data, across all 4 Northwind sibling classes, in the default query mode.

#### Branch 3A — Hypothesis A holds (Shape 3 shares BindResultMember/join-scope machinery with Shapes 1/2)

- [ ] **Step 1: Read the collection-Include branch this fix must mirror**

Read `MongoProjectionBindingExpressionVisitor.cs`'s `case IncludeExpression` block, specifically the
`if (!includableNavigation.IsEmbedded() && includableNavigation.IsCollection)` branch and its
`LookupExpression`/`AddLookup` call. This is the shape the reference-Include branch (currently a no-op besides
visiting children) must mirror.

- [ ] **Step 2: Add lookup registration for a reference Include reached through this visitor**

In the same `case IncludeExpression` block's non-collection branch (currently just
`_includedNavigations.Push(...); base.VisitExtension(includeExpression); ...` with no lookup call), add the
equivalent `LookupExpression`/`AddLookup` registration for a reference navigation — same pattern as the
collection branch, using the reference navigation's target entity type and foreign key instead of the
collection navigation's. Rewrite the `IncludeExpression` into a shape
`MongoProjectionBindingRemovingExpressionVisitor`/`MongoStreamingEntityMaterializerRewriter` can already read
for an ordinary (non-join-scope) reference Include — confirm by reading how the two ALREADY-PASSING bare
reference-Include tests' code path builds that same shape (Task 1's Step 6 findings tell you whether this is
the same visitor method or a different one; if different, mirror that method's output shape exactly, not this
one's guess).

- [ ] **Step 3: Remove (or widen) `BindResultMember`'s `allCollectionIncludes` guard for the reference case**

`BindResultMember` (`MongoQueryableMethodTranslatingExpressionVisitor.cs` ~line 3390) currently declines
(falls through to `BindSelectManyMember`'s fallback) whenever `allCollectionIncludes` is `false`. Now that
Step 2 gives a reference Include its own lookup, change this to rebind a reference-Include wrapper against
that new lookup instead of unconditionally declining — keep the existing collection-Include rebind-by-index
path intact for `IsCollection: true` wrappers, add a parallel rebind-by-lookup-alias path for
`IsCollection: false` wrappers, and only fall through to the pre-existing fallback if NEITHER path applies
(e.g. an embedded/owned reference nav, which Step 2's `IsEmbedded()` check already excludes upstream).

- [ ] **Step 4: Promote Shape 1's existing decline-only test to a correctness test**

`Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`
(`NativeJoinTests.cs:1797`) currently accepts either a throw or correct data. Once Steps 2-3 land, it must
NEVER throw for this shape — tighten the assertion to require `threw == false` and the populated-Owner
assertions unconditionally. Rename it to drop "declines_rather_than" from the name (e.g.
`Whole_entity_leaf_that_is_also_reference_included_materializes_correctly`) since the decline is no longer the
expected behavior; update the doc comment to match.

- [ ] **Step 5: Add a permanent test for Shape 4 (the no-join control repro)**

Add to `NativeJoinTests.cs` (or, if Task 1's findings show Shape 4 does not touch join-scope machinery at
all, to `NativeReferenceIncludeTests.cs` instead — match the file to the arm actually exercised):

```csharp
[Fact]
public void Reference_Include_with_no_join_in_the_query_materializes_correctly()
{
    // Design doc Shape 4: db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId }) has no .Join()/
    // .LeftJoin() at all, grouped with Shape 3 as "likely the same bug, isolated". Must materialize a real
    // Owner, never null, now that Step 2/3's fix (or 3B's fix) is in place.
    var seed = SeedOwnersAndOrders();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Reference_Include_with_no_join_in_the_query_materializes_correctly));

    var results = db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })
        .AsEnumerable().ToList();

    Assert.NotEmpty(results);
    Assert.All(results, x => Assert.NotNull(x.o.Owner));
}
```

- [ ] **Step 6: Regenerate the two target spec tests' baselines and run them, plus Shape 4**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Include_reference_when_entity_in_projection|FullyQualifiedName~Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join"
```

Then re-run without the rewrite flag to confirm they pass and the diff to the 4 sibling `.cs` files' committed
`AssertMql` bodies is the expected new pipeline shape (see the spec-conformance-reviewer's own baseline
conventions in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` if the rewrite behaves
unexpectedly).

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Reference_Include_with_no_join_in_the_query_materializes_correctly"
```

Expected: all 4 sibling classes' full suites PASS (not just the two target tests — this area's shared
machinery can regress siblings), and Shape 4's new test PASSES.

- [ ] **Step 7: Run the full regression sweep**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```

Expected: PASS, including `NativeReferenceIncludeTests`, `NativeJoinTests`, `NativeJoinScopeProjectionBinderTests`.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindEFPropertyIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs
git commit -m "$(cat <<'EOF'
EF-322: materialize a reference Include on a native join-scope leaf correctly

Task 1's investigation confirmed Hypothesis A: Include_reference_when_entity_in_projection
and the mixed left-join Include test both reach BindResultMember's wrapped-leaf-join
arm, the same machinery root cause A2 fixed for collection Includes. Gives a reference
Include reached through MongoProjectionBindingExpressionVisitor its own $lookup
registration (mirroring the collection branch) and widens BindResultMember to rebind
against it instead of unconditionally declining.
EOF
)"
```

#### Branch 3B — Hypothesis B holds (Shape 3 is `TryConfirmReferenceIncludeChain`'s own, separate bug)

- [ ] **Step 1: Read `TryConfirmReferenceIncludeChain`'s full body and what runs after it**

Read `MongoQueryableMethodTranslatingExpressionVisitor.cs:1771` onward, and — since the method does not
`return` after confirming — trace what `TranslateSelect` does with a confirmed chain afterward (the generic
shaper-fold logic at the end of that method). Identify the exact point where a confirmed chain's reference
navigation should be, but isn't, resolved to a real value.

- [ ] **Step 2: Identify why a CONFIRMED chain still materializes null**

Likely candidates to check first (in order of how the design doc frames the mechanism): (a) confirmation
registers the join/lookup metadata but the shaper-fold path never builds a
`StructuralTypeShaperExpression`/`ProjectionBindingExpression` pointing at it — compare against how the
collection-Include lookup path (root cause A2, `MongoProjectionBindingExpressionVisitor`'s `IsCollection`
branch) both registers AND rewrites the shaper; (b) the lookup registers under one alias but the shaper reads
a different one (the `"_outer"`/`"_inner"` naming vs. `UsesDriverJoinFields` mismatch the design doc's Shape 3
section flags) — check `MongoQueryExpression.UsesDriverJoinFields`'s computed value at this point in
execution against what the shaper-fold path assumes.

- [ ] **Step 3: Fix the identified gap**

Write the fix once Step 2 names the exact mismatch — this cannot be pre-specified further without Task 1/this
branch's own Step 1-2 findings in hand. Whatever the fix, it must not touch `BindResultMember`'s
`allCollectionIncludes` guard or `MongoProjectionBindingExpressionVisitor`'s `IsCollection` branch (both
committed, root-cause-A2 territory, out of scope per this plan's Global Constraints) unless Step 1's trace
proves they are the SAME code executing for this shape too (in which case, stop, re-read the design doc's
Hypothesis A framing — this would mean the finding was wrong and 3A applies instead).

- [ ] **Step 4: Add a permanent test for Shape 4 (the no-join control repro)**

Same test and rationale as 3A Step 5 — add
`Reference_Include_with_no_join_in_the_query_materializes_correctly` to whichever test file matches the arm
Step 1 traced (likely `NativeReferenceIncludeTests.cs` under Hypothesis B, since Shape 4 has no join at all).

- [ ] **Step 5: Regenerate the two target spec tests' baselines and run them, plus Shape 4**

Same commands as 3A Step 6.

- [ ] **Step 6: Run the full regression sweep**

Same command as 3A Step 7.

- [ ] **Step 7: Commit**

```bash
git add <files Step 3 touched>
git commit -m "$(cat <<'EOF'
EF-322: fix TryConfirmReferenceIncludeChain's post-confirmation materialization gap

Task 1's investigation confirmed Hypothesis B: Include_reference_when_entity_in_projection
and the mixed left-join Include test reach TryConfirmReferenceIncludeChain's own,
separate machinery, unrelated to the join-scope BindResultMember arm root cause A2
touched. <fill in the specific mismatch Step 2 found>.
EOF
)"
```

---

### Task 4: Whole-branch regression sweep and cleanup

**Files:** none new — this task runs the full suite and, if Task 2's guard or Task 3's fix touched any
version-conditional code path, repeats against EF8/EF9 too.

**Interfaces:**
- Consumes: everything committed in Tasks 1-3.
- Produces: a verified-clean branch state, ready for `superpowers:finishing-a-development-branch`.

- [ ] **Step 1: Run the full EF10 suite**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

Expected: PASS (0 failures).

- [ ] **Step 2: If Task 2 or Task 3 touched any file with an `#if EF8`/`#if EF9`/`#if !EF8` guard, run all three EF versions**

Check first:

```bash
git diff main...HEAD --name-only | xargs grep -l "#if EF8\|#if EF9\|#if !EF8" 2>/dev/null
```

If any file is listed, invoke the `/test-all` skill (builds and runs against EF8, EF9, EF10 in parallel). If
nothing is listed, skip this step — no version-conditional code was touched.

- [ ] **Step 3: Confirm the two already-passing bare reference-Include tests are still passing**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Optional_reference_Include_goes_native_with_a_left_outer_unwind|FullyQualifiedName~Required_reference_Include_goes_native_with_an_inner_unwind"
```

Expected: PASS.

- [ ] **Step 4: Delete the temporary findings doc's staleness risk — leave it in place, but cross-reference it**

No code change; add one line to `docs/superpowers/specs/2026-09-25-reference-include-materialization-design.md`
noting it is now implemented, with a pointer to this plan and the findings doc, so a future reader doesn't
re-open a closed investigation.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-25-reference-include-materialization-design.md
git commit -m "EF-322: mark reference-Include materialization design doc as implemented"
```
