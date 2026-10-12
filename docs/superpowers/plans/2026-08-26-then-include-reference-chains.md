# ThenInclude Reference Chains Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make native single-level reference `Include` admit a genuine reference `ThenInclude` chain — e.g.
`db.Roots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ThenInclude(l => l.Tip)` — instead of declining
outright, without changing the disposition of anything already declined for a real reason (filtered Include,
composite keys, self-referencing/renamed/colliding-FK edge cases already covered by `Ef379RootNavigationMisroutingTests`,
a sibling hanging off a `ThenInclude`, or a collection `ThenInclude`).

**Architecture:** `ThenInclude` nests via `IncludeExpression.NavigationExpression`, not `EntityExpression` —
a structurally different axis from the sibling-Include nesting this session already generalized. The
join/lookup-registration machinery (`TranslateJoinCore`/`RebindInnerShaperToOuterQuery`) already handles
N-hop transitive joins correctly and unconditionally (proven by `Ef372DeepReferenceIncludeTests`/
`Ef373InterleavedPagingTests`/`Ef379RootNavigationMisroutingTests`, which exercise up to 4 hops — but **only**
via the driver-LINQ fallback bridge today, since `MongoQueryMode.NativeOnly` throws for this shape as
currently written; the Query `AGENTS.md`'s claim that this "scopes its `$lookup` correctly" is true of the
*fallback*, not of native execution, and needs correcting).

A throwaway, fully-reverted spike this session (production code temporarily mutated, run, observed, `git
checkout`'d back — nothing committed) empirically confirmed the one real unknown: **once the recognizer
admits a transitive level, both the join-registration machinery's shaper-building (already used
unconditionally at join time) and a widened `MongoSelectLowerer.AppendLookupStages` dispatch condition
correctly produce a $lookup with the transitively-prefixed `localField` (`_lookup_Mid.LeafId`) and correct
data** — no new shaper code is needed, only a lowerer dispatch widening. The real work is entirely in the
recognizer/confirm layer: `TryWalkIncludeChain` must recurse into `NavigationExpression` (not just
`EntityExpression`), correctly distinguishing three things at each step: an owned/embedded auto-include
(transparent, not a new chain entry — reuses the existing `HasNonEmbeddedThenInclude` check unchanged), a
genuine non-embedded reference `ThenInclude` (a new chain entry, structurally validated), and anything else
(collection `ThenInclude`, a sibling hanging off a `ThenInclude`, a structural mismatch — all declined, out
of scope for this slice).

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit functional tests against a real MongoDB.

**Spec:** No separate spec doc — reuses the existing `Ef372DeepReferenceIncludeTests`/
`Ef373InterleavedPagingTests`/`Ef379RootNavigationMisroutingTests` fixtures and their documented near-miss
history as the design record; this plan's Architecture section is the design summary.

## Global Constraints

- Do not touch `TranslateJoinCore`/`RebindInnerShaperToOuterQuery`/`PeelEmbeddedSegments`/
  `AnalyzeKeySelectorTarget` — the spike confirmed this machinery is already correct and unconditional for
  transitive hops. Touching it is out of scope and a red flag if a task believes it's necessary.
- Every existing decline in `Ef379RootNavigationMisroutingTests` (self-referencing two-hop, renamed-FK
  transitive hop, colliding-FK-name transitive hop) currently asserts `NativeOnly` **declines**. This plan's
  Task 3 explicitly re-measures each one: if the underlying join-registration fix that test guards (already
  shipped, per that file's own header comment) makes the shape safe to admit, its "still declines" test
  should be *flipped* to a positive assertion in this same task, not left declining by accident. Do not
  assume either direction — run each one and look at the result.
- A collection navigation encountered while following a `ThenInclude` chain (a collection `ThenInclude` off a
  reference `Include`) must keep declining — out of scope for this slice.
- A sibling `Include` hanging off a `ThenInclude` level (`thenInclude.EntityExpression is IncludeExpression`)
  must keep declining — out of scope for this slice.
- `IsStreamableReference` (`LookupExpression.cs`) must NOT be redefined — it is also used by
  `AllPendingLookupsAreStreamable`/`StreamingEligibility` to correctly keep transitive-lookup queries off the
  one-pass streaming materializer (unverified, deliberately deferred) and route them to the DOM shaper
  instead. Only `MongoSelectLowerer.AppendLookupStages`'s own dispatch condition changes.

---

## Task 1: Generalize the recognizer to walk `NavigationExpression`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  - `TryWalkIncludeChain` (private, shared by `TryGetReferenceIncludeChain`/`TryGetMixedReferenceAndCollectionIncludeChain`).
  - `TryGetReferenceIncludeChain` (add an overload exposing `transitiveLevels`; keep the existing 1-arg
    overload for the existing unit tests).
  - `TryGetMixedReferenceAndCollectionIncludeChain` (add a `transitiveLevels` out param).
  - `TryConfirmReferenceIncludeChain` (add a `transitiveLevels` parameter; drop the now-superseded
    `HasNonEmbeddedThenInclude(include.NavigationExpression)` per-level check; make the
    `DeclaringEntityType` check conditional on `!transitiveLevels.Contains(include)`).
  - The trailing-Select dispatch call sites (both branches that call `TryConfirmReferenceIncludeChain`).
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs`

**Interfaces:**
- Produces: `TryGetReferenceIncludeChain(LambdaExpression selector, out HashSet<IncludeExpression> transitiveLevels) : List<IncludeExpression>?`
  (new overload; the existing `TryGetReferenceIncludeChain(LambdaExpression selector) : List<IncludeExpression>?`
  stays, delegating to the new one and discarding `transitiveLevels`).
- Produces: `TryGetMixedReferenceAndCollectionIncludeChain(LambdaExpression selector, out List<IncludeExpression> referenceLevels, out HashSet<IncludeExpression> transitiveLevels, out IncludeExpression? collectionLevel) : bool`.
- Produces: `TryConfirmReferenceIncludeChain(MongoQueryExpression mongoQueryExpression, List<IncludeExpression> chain, HashSet<IncludeExpression> transitiveLevels) : bool`.
- Consumes (unchanged): `HasNonEmbeddedThenInclude(Expression)`, `LookupExpression.GetLookupAlias(INavigation)`
  (returns `_lookup_<navigation.Name>`, unprefixed regardless of depth — confirmed empirically via the
  session's spike, whose captured MQL showed a plain `"_lookup_Leaf"` alias with only the `localField`
  transitively prefixed), `MongoQueryExpression.Joins`/`GetPendingLookups()`/`AddLookup`/`InnerCollections`.

- [ ] **Step 1: Write failing unit tests using the real `Ef372`-style model shape (hand-built expression trees, no DB)**

Add to `ReferenceIncludeTestTrees` in `ReferenceIncludeRecognizerTests.cs` a new builder for a linear
2-hop `ThenInclude` chain and a 2-hop chain with an embedded hop in between (mirroring `Buyer.Address.Region`
from EF-407, to prove the embedded-transparency rule still holds):

```csharp
private class Deep
{
    public ObjectId Id { get; set; }
}

private class Owned
{
    public ObjectId DeepId { get; set; }
    public Deep Deep { get; set; } = null!;
}

// Reuse Order/Customer/Vendor from the existing builders; add a real, non-embedded navigation directly
// off Customer for the plain ThenInclude case, and an embedded navigation off Customer wrapping a further
// real navigation, for the embedded-transparency case.
private class Customer
{
    public int Id { get; set; }
    public int DeepId { get; set; }
    public Deep Deep { get; set; } = null!;
    public Owned Owned { get; set; } = null!;
}
```

(Adjust the existing `Customer`/model-building code in that file as needed — check its current shape first
via `Read` before editing, since it's shared by the earlier sibling-Include tests and must keep working
unchanged for those.)

```csharp
/// <summary>
/// Builds <c>ti =&gt; Include(ti.Outer, Order.Customer, Include(ti.Inner, Customer.Deep, ti.Inner.Deep))</c>
/// — a linear 2-hop reference ThenInclude chain (Order.Include(Customer).ThenInclude(Deep)).
/// </summary>
public static LambdaExpression BuildThenIncludeChain(bool embeddedHopInBetween)
```

- [ ] **Step 2: Run to verify RED**

Expected: compile error (`TryGetReferenceIncludeChain` doesn't have the 2-arg overload yet) or, once you
stub the overload to just delegate without generalizing, a runtime assertion failure (chain recognized as
length 1, not 2, or `null`).

- [ ] **Step 3: Implement the walker generalization**

Replace `TryWalkIncludeChain`'s body with (exact code — read the current file first to confirm nothing has
drifted, per the "no placeholders" rule; this is the full replacement, not a diff fragment):

```csharp
private static bool TryWalkIncludeChain(
    LambdaExpression selector,
    out List<IncludeExpression> referenceLevels,
    out HashSet<IncludeExpression> transitiveLevels,
    out IncludeExpression? collectionLevel)
{
    referenceLevels = [];
    transitiveLevels = [];
    collectionLevel = null;

    if (selector.Parameters.Count != 1
        || !selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal))
    {
        return false;
    }

    var parameter = selector.Parameters[0];
    var body = selector.Body;

    while (body is IncludeExpression { Navigation: INavigation navigation } include)
    {
        if (navigation.IsEmbedded())
        {
            return false;
        }

        if (navigation.IsCollection)
        {
            if (collectionLevel != null)
            {
                return false;
            }

            collectionLevel = include;
        }
        else
        {
            referenceLevels.Add(include);
        }

        // Follow a linear ThenInclude chain hanging off THIS level, via NavigationExpression — the axis
        // a sibling Include never uses (that's EntityExpression, the outer while loop below).
        var current = include;
        var currentTargetType = navigation.TargetEntityType;
        while (current.NavigationExpression is IncludeExpression { Navigation: INavigation thenNav } thenInclude)
        {
            if (thenNav.IsEmbedded())
            {
                // An owned auto-include (or chain of them) on the target — lives inside the same
                // document, no lookup needed (EF-368). Verify nothing REAL is nested past it — the
                // existing rule, unchanged — and stop following THIS level's ThenInclude chain either
                // way (embedded or not, this is where it ends for this sibling).
                if (HasNonEmbeddedThenInclude(thenInclude))
                {
                    return false;
                }

                break;
            }

            if (thenNav.IsCollection
                || thenInclude.EntityExpression is IncludeExpression
                || thenNav.DeclaringEntityType != currentTargetType)
            {
                // A collection ThenInclude, a sibling hanging off a ThenInclude, or a structural
                // mismatch (this hop doesn't declare on the previous hop's target) — out of scope for
                // this recognizer; decline the whole chain rather than mishandle it.
                return false;
            }

            referenceLevels.Add(thenInclude);
            transitiveLevels.Add(thenInclude);
            current = thenInclude;
            currentTargetType = thenNav.TargetEntityType;
        }

        body = include.EntityExpression;
    }

    if (referenceLevels.Count == 0 && collectionLevel == null)
    {
        return false;
    }

    var baseExpr = body;
    while (baseExpr is MemberExpression { Member.Name: "Outer" } outerAccess)
    {
        baseExpr = outerAccess.Expression;
    }

    return baseExpr == parameter;
}
```

- [ ] **Step 4: Update `TryGetReferenceIncludeChain` and `TryGetMixedReferenceAndCollectionIncludeChain`**

Replace:
```csharp
internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
{
    if (!TryWalkIncludeChain(selector, out var referenceLevels, out var collectionLevel)
        || collectionLevel != null
        || referenceLevels.Count == 0)
    {
        return null;
    }

    return referenceLevels;
}
```
with:
```csharp
internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
    => TryGetReferenceIncludeChain(selector, out _);

internal static List<IncludeExpression>? TryGetReferenceIncludeChain(
    LambdaExpression selector, out HashSet<IncludeExpression> transitiveLevels)
{
    if (!TryWalkIncludeChain(selector, out var referenceLevels, out transitiveLevels, out var collectionLevel)
        || collectionLevel != null
        || referenceLevels.Count == 0)
    {
        transitiveLevels = [];
        return null;
    }

    return referenceLevels;
}
```

Replace:
```csharp
internal static bool TryGetMixedReferenceAndCollectionIncludeChain(
    LambdaExpression selector,
    out List<IncludeExpression> referenceLevels,
    out IncludeExpression? collectionLevel)
{
    if (!TryWalkIncludeChain(selector, out referenceLevels, out collectionLevel)
        || collectionLevel == null
        || referenceLevels.Count == 0)
    {
        referenceLevels = [];
        collectionLevel = null;
        return false;
    }

    return true;
}
```
with:
```csharp
internal static bool TryGetMixedReferenceAndCollectionIncludeChain(
    LambdaExpression selector,
    out List<IncludeExpression> referenceLevels,
    out HashSet<IncludeExpression> transitiveLevels,
    out IncludeExpression? collectionLevel)
{
    if (!TryWalkIncludeChain(selector, out referenceLevels, out transitiveLevels, out collectionLevel)
        || collectionLevel == null
        || referenceLevels.Count == 0)
    {
        referenceLevels = [];
        transitiveLevels = [];
        collectionLevel = null;
        return false;
    }

    return true;
}
```

Update the XML doc comments on both to mention the new `ThenInclude`-chain capability in one sentence each
(follow the existing comment style/tone in the file — factual, cites the mechanism, no marketing language).

- [ ] **Step 5: Update `TryConfirmReferenceIncludeChain`**

Change the signature to `TryConfirmReferenceIncludeChain(MongoQueryExpression mongoQueryExpression, List<IncludeExpression> chain, HashSet<IncludeExpression> transitiveLevels)`.
Inside the `foreach (var include in chain)` loop, replace:
```csharp
            if (navigation.ForeignKey.Properties.Count != 1                        // composite FK
                || navigation.ForeignKey.PrincipalKey.Properties.Count != 1        // composite PK
                || HasNonEmbeddedThenInclude(include.NavigationExpression)         // a real ThenInclude riding along
                || navigation.DeclaringEntityType != mongoQueryExpression.CollectionExpression.EntityType
                || !mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType))
            {
                return false;
            }
```
with:
```csharp
            // HasNonEmbeddedThenInclude is no longer checked here — TryWalkIncludeChain is now the sole
            // authority on which ThenInclude nesting is admissible (embedded-only chains stay transparent;
            // a genuine non-embedded reference ThenInclude is admitted as its own chain entry instead of
            // declining). DeclaringEntityType is checked against the query ROOT only for a root-level
            // (non-transitive) entry — a transitive entry's declaring type was already verified against
            // its PARENT's target type, structurally, inside the walker itself.
            if (navigation.ForeignKey.Properties.Count != 1                        // composite FK
                || navigation.ForeignKey.PrincipalKey.Properties.Count != 1        // composite PK
                || (!transitiveLevels.Contains(include)
                    && navigation.DeclaringEntityType != mongoQueryExpression.CollectionExpression.EntityType)
                || !mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType))
            {
                return false;
            }
```
Update the method's XML doc comment: the paragraph currently describing why a `ThenInclude` declines (the
one starting "`TryGetReferenceIncludeChain` only inspects `IncludeExpression.EntityExpression` nesting...")
needs rewriting to describe the NEW disposition (a non-embedded `ThenInclude` is now admitted as its own
chain entry; only a collection `ThenInclude` or a sibling-under-`ThenInclude` still declines) rather than
describing the old blanket decline.

- [ ] **Step 6: Update the two call sites in the trailing-Select dispatch**

Find (there are two, one for `TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain` and one for
`TryGetMixedReferenceAndCollectionIncludeChain`) and update both to capture and pass `transitiveLevels`:
```csharp
        if (TryGetReferenceIncludeChain(selector, out var transitiveLevels) is { } referenceIncludeChain)
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, referenceIncludeChain, transitiveLevels))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (TryGetMixedReferenceAndCollectionIncludeChain(
                     selector, out var mixedReferenceLevels, out var mixedTransitiveLevels, out _))
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, mixedReferenceLevels, mixedTransitiveLevels))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
```
(Read the current exact surrounding code first — confirm variable names/comments haven't drifted since the
reference+collection combo work — and preserve the existing explanatory comments on each branch, updating
only what's needed for the new parameter.)

- [ ] **Step 7: Build and run the unit tests to verify GREEN**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -20
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ReferenceIncludeRecognizerTests"
```
Expected: all pass, including the new Step-1 tests and every pre-existing one (sibling chains, reference+collection
rejection, double-hop, etc. — the refactor must not change their observable behavior).

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs
git commit -m "EF-392: recognize reference ThenInclude chains (recognizer only, not yet wired to lowerer)"
```

---

## Task 2: Widen the lowerer's dispatch for a transitive reference lookup

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef372DeepReferenceIncludeTests.cs`

**Interfaces:**
- Consumes (unchanged): `LookupExpression.IsReference`, `.HasPipeline`, `.PreserveNullAndEmptyArrays`.
- Does NOT change `LookupExpression.IsStreamableReference` (still used by `AllPendingLookupsAreStreamable`
  for the separate streaming-eligibility decision — a transitive-lookup query must keep routing to the DOM
  shaper, not the one-pass streaming materializer, since streaming correctness for this shape is unverified
  and deliberately out of scope here).

- [ ] **Step 1: Write the failing test**

Add to `Ef372DeepReferenceIncludeTests.cs`, right after `T2` (`Three_hop_reference_ThenInclude_prefixes_the_third_localField`):

```csharp
[Fact]
public void Two_hop_reference_ThenInclude_goes_native()
{
    // EF-392: this exact shape declined under NativeOnly before this change (the recognizer now admits
    // it, per Task 1; this test proves the lowerer can actually emit the transitive $lookup too).
    using var db = CreateContext(MongoQueryMode.NativeOnly,
        nameof(Two_hop_reference_ThenInclude_goes_native), out var spyLogger);

    var results = db.Roots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

    Assert.Equal(3, results.Count);
    Assert.All(results, r => Assert.NotNull(r.Mid));
    Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
    Assert.Equal(["L1", "L2", "L3"], results.Select(r => r.Mid.Leaf.Label).OrderBy(x => x));

    var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
    Assert.Contains("\"localField\" : \"MidId\"", mql);
    Assert.Contains("\"localField\" : \"_lookup_Mid.LeafId\"", mql);
}
```

(Check the seed data first — `Leaf.Label` values `"L1"`/`"L2"`/`"L3"` are assumed from the existing `T1`
test's use of `Tip.Label` values `"T1"`/`"T2"`/`"T3"`; read `DeepChainDbContext`'s seed method to confirm the
actual `Leaf` labels before trusting this literal — adjust if different.)

- [ ] **Step 2: Run to verify RED**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -10
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Two_hop_reference_ThenInclude_goes_native"
```
Expected: FAIL with `NativeTranslationNotSupportedException` ("Native pipeline does not support lookup for
navigation 'Leaf' ...") — the recognizer now admits the shape (Task 1), so it reaches the lowerer, which
still hard-throws on the transitive lookup until this task's fix lands.

- [ ] **Step 3: Widen the dispatch condition**

In `MongoSelectLowerer.AppendLookupStages`, change:
```csharp
            if (lookup.IsStreamableReference)
            {
```
to:
```csharp
            if (lookup.IsReference && !lookup.HasPipeline)
            {
```
Update the surrounding comment (currently starting "The $unwind must follow the navigation's own
requiredness...") to note that this branch now also covers a TRANSITIVE reference lookup (`localField`
prefixed with a prior lookup's alias) — the stage-emission code itself (`$lookup` + `$unwind`) is identical
either way; only `IsStreamableReference`'s own streaming-eligibility meaning (unchanged, used elsewhere)
excludes the transitive case.

- [ ] **Step 4: Run to verify GREEN**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -10
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Two_hop_reference_ThenInclude_goes_native"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef372DeepReferenceIncludeTests.cs
git commit -m "EF-392: widen MongoSelectLowerer's reference-lookup dispatch to cover transitive hops"
```

---

## Task 3: Re-measure and update the full `Ef372`/`Ef373`/`Ef379` suites, plus `AGENTS.md`

**Files:**
- Modify (as needed, per what Step 1 actually finds — do not pre-guess which specific tests flip):
  `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/Ef372DeepReferenceIncludeTests.cs`,
  `Ef373InterleavedPagingTests.cs`, `Ef379RootNavigationMisroutingTests.cs`.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (correct the overclaim; add the new capability).

**Interfaces:** none new — this task is measurement-driven test/doc updates.

- [ ] **Step 1: Run the full three files under the current (post Task 1+2) code and record every result**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Ef372|FullyQualifiedName~Ef373|FullyQualifiedName~Ef379" 2>&1 | tee /tmp/ef372-379-results.txt
```
Read every failure's message. For each, classify: (a) an existing `..._still_declines_under_NativeOnly`-style
test that now fails because the shape legitimately goes native (a WIN — flip the assertion to positive,
matching the pattern in Task 2's own new test) — but only if the shape is a PLAIN linear reference
`ThenInclude` chain within this slice's scope; (b) a genuine regression (a shape that must still decline
but no longer does, or — far more serious — now returns WRONG data) — STOP, do not proceed to Step 2, return
to Task 1 and find the missing guard; (c) unrelated/flaky — investigate before dismissing.

- [ ] **Step 2: For each Category-(a) test, flip it and verify it proves DATA correctness, not just "doesn't throw"**

For a test like `Colliding_fk_name_transitive_hop_still_declines_under_NativeOnly`, do not just delete the
`Assert.Throws` and call it done — check whether a PAIRED data-correctness test already exists for this
exact shape (e.g. `Colliding_fk_name_transitive_hop_reads_the_intermediate_not_the_root(mode)`, a `[Theory]`
over `Native`/`DriverLinq`). If the paired test already asserts the right data and now also runs the query
NATIVELY (since `Native` mode no longer needs to fall back), that data assertion is now doing double duty —
rename/re-comment the declining test to something like `..._now_confirmed_native_under_NativeOnly` and
assert success (`NativeOnly` doesn't throw) rather than deleting it outright, so the file keeps a fast,
explicit "goes native" proof point distinct from the data-correctness theory.

- [ ] **Step 3: Full regression across all three EF versions**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" 2>&1 | tail -5
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" 2>&1 | tail -5
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: 0 failures on all three. Any spec-test `AssertMql` baseline failure that reaches `AssertBaseline`
(i.e. the data assertion already passed) is expected MQL drift from shapes newly going native — rebase via
`EF_TEST_REWRITE_BASELINES=1` exactly as done for the two earlier EF-392 slices this session, then rebuild
and re-run to confirm genuinely green. Any OTHER failure (a data assertion failing before reaching
`AssertBaseline`, or a crash) is a real regression — stop and investigate via systematic-debugging, do not
paper over it.

- [ ] **Step 4: Correct the `AGENTS.md` overclaim and document the new capability**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s `Include` bullet, the sentence "A multi-hop (3+)
reference-join chain scopes its `$lookup` correctly by resolving each hop's join-hop kind..." currently
implies this already goes native. Rewrite it to state plainly that a linear reference `ThenInclude` chain
now goes native (citing `TryWalkIncludeChain`'s `NavigationExpression` recursion and the widened
`MongoSelectLowerer` dispatch), and that a collection `ThenInclude` or a sibling hanging off a `ThenInclude`
still falls back. Remove or correct the implication that the join-hop-classification machinery alone was
ever sufficient for native execution — it was only ever sufficient for the FALLBACK bridge until this slice.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "EF-392: re-measure Ef372/373/379 suites for ThenInclude chains; correct AGENTS.md overclaim"
```

---

## Task 4: Final full-suite verification and push

**Files:** none — verification only.

- [ ] **Step 1: Full three-EF-version regression one more time, clean**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: 0 failures across unit/functional/spec on all three.

- [ ] **Step 2: `git status` clean, no stray spike artifacts, then push**

```bash
git status
git push origin EF-322-Native-LINQ-rebased
```
