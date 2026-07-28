# EF-368 — Native Single-Level Reference `Include` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a single-level, single reference `Include` (e.g. `db.Orders.Include(o => o.Customer)`) execute on the provider's native MQL translator instead of falling back to the driver's LINQ provider.

**Architecture:** Register a forced-unwind reference `LookupExpression` for the recognized shape so `UsesDriverJoinFields` computes `false`. Three existing consumers — the native lowerer, the DOM shaper, and the driver-LINQ fallback — then all agree on the `_lookup_<Nav>` field name, so both execution modes read the same document shape. No new emission machinery; no shaper or lowerer change.

**Tech Stack:** C#, EF Core 8/9/10 (build configurations `Debug EF8` / `Debug EF9` / `Debug EF10`), MongoDB C# driver 3.10.0, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-04-native-reference-include-design.md`

## Global Constraints

- **Branch:** `EF-368`. Task 1 commits to `NativeQueryOngoing`; Tasks 2–8 commit to `EF-368`.
- **Three EF majors must stay green.** Every task's verification runs all three: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build` (and `EF9`, `EF8`). The `/test-all` skill runs all three in parallel.
- **Run tests with `MONGODB_URI` and `ATLAS_URI` unset** so TestContainers boots an isolated `mongodb/mongodb-atlas-local` per test process.
- **`<Nullable>enable</Nullable>` on `src/`** — annotate new members accordingly.
- **Preserve file BOMs.**
- **Never use xUnit `Skip`** to green a spec failure. Baseline it with a `// Fails:` comment plus a `docs/failing-spec-tests.md` entry.
- **Required-navigation tests are ungated** (all three majors). Only optional-FK cases carry `#if !EF8 && !EF9`.
- **Multi-EF conditionals** use the `EF8` / `EF9` / `EF10` define constants.
- Commit messages start with `EF-368:` (Task 1's starts with `EF-370:`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/.../Query/Expressions/MongoSelectDefinition.cs` | Two provisional signals (`MarkSawCandidateReferenceIncludeJoin`, `MarkReferenceIncludeConfirmed`) and the `Route` term that makes them default-deny. |
| `src/.../Query/NativeTranslation/NativeSlotPopulator.cs` | Join arm: record a candidate instead of marking non-native. |
| `src/.../Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | `IsSingleLevelReferenceIncludeSelector` recognizer, the `TranslateSelect` pass-through + registration, and the decline conditions. |
| `tests/.../UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs` | Recognizer unit tests, including the user-join double-hop refutation. |
| `tests/.../FunctionalTests/Query/NativeReferenceIncludeTests.cs` | Native MQL pinning, both materializers, declines with tripwires. |
| `tests/.../FunctionalTests/Query/RequiredNavigationUnwindTests.cs` | Extended with `Native == DriverLinq == NativeOnly` differential assertions over the dangling-FK seed. |
| `tests/.../FunctionalTests/Query/QueryModeGateIncludeTests.cs` | Re-baselined: reference Include no longer falls back. |
| `tests/.../SpecificationTests/Query/Northwind*IncludeQueryMongoTest.cs` | Re-baselined MQL assertions. |
| `docs/failing-spec-tests.md`, `src/.../Query/AGENTS.md` | Baseline rows this slice moves; two doc corrections. |

---

### Task 1: Port EF-370 onto the native branch

Prerequisite from spec §2. This lands on `NativeQueryOngoing`, **not** `EF-368` — it is EF-370's work and PR #324 carries it independently.

**Files:**
- Modify: whatever `git cherry-pick` touches (15 files; two conflict)
- Conflicts: `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs`, `docs/failing-spec-tests.md`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `LookupExpression.PreserveNullAndEmptyArrays { get; init; } = true`; the `_injectAfterBaseSourceLookups` reattachment in `MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs`; the test class `RequiredNavigationUnwindTests` with its dangling-FK seed. Every later task depends on these existing.

- [ ] **Step 1: Create the working branch from the native tip**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout NativeQueryOngoing
git branch EF-370-on-native-presquash-safety   # safety net before any history work
git status --short                              # MUST be clean before continuing
```

- [ ] **Step 2: Cherry-pick EF-370 and expect exactly two conflicts**

```bash
git cherry-pick 4a9ec84
git diff --name-only --diff-filter=U
```

Expected output — exactly these two, nothing else:
```
docs/failing-spec-tests.md
src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs
```

If any other file conflicts, STOP and report — the base has moved since this plan was written.

- [ ] **Step 3: Resolve `LookupExpression.cs` — keep BOTH members**

This is an adjacent-insertion collision: the native side adds `IsNativeCollectionLookup`, EF-370 adds `PreserveNullAndEmptyArrays`, and both land immediately above the XML doc for the injection-point property. Keep both, in this order, deleting all conflict markers:

```csharp
    public bool IsNativeCollectionLookup
        => Navigation.IsCollection
           && !HasPipeline
           && !ForceUnwind
           && As == GetLookupAlias(Navigation);

    /// <summary>
    /// Whether the <c>$unwind</c> that follows this <c>$lookup</c> uses
    /// <c>preserveNullAndEmptyArrays: true</c> — i.e. whether the join is LEFT-OUTER (the principal
    /// document survives when nothing matched) or INNER (it is dropped).
    /// <para>
    /// Defaults to <see langword="true"/>: an <c>Include</c> must never drop principals, which is the
    /// semantics every non-join registration site wants, so a site that does not think about this flag
    /// gets the conservative behaviour. The join-translation path overrides it from the LINQ operator EF
    /// actually produced — <c>LeftJoin</c>/<c>GroupJoin</c> are left-outer, a plain <c>Join</c> is inner.
    /// MongoDB enforces no referential integrity, so a dangling foreign key is an ordinary data state and
    /// the distinction is observable.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see langword="init"/>-only: this is compile-time state on an object reused across executions, so it
    /// must be written exactly once, at registration.
    /// </remarks>
    public bool PreserveNullAndEmptyArrays { get; init; } = true;
```

- [ ] **Step 4: Resolve `docs/failing-spec-tests.md` by keeping both sides' rows**

Both branches add rows. Take the union. Do **not** attempt to reconcile the totals column — its basis is lost and it reconciles under no rule (see spec §7.1). If the two sides give a different total for the same row, keep the native branch's and add a `<!-- EF-370 port: total not reconciled, see EF-368 design §7.1 -->` comment on that row.

- [ ] **Step 5: Complete the cherry-pick and build all three majors**

```bash
git add docs/failing-spec-tests.md src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs
git cherry-pick --continue --no-edit
for cfg in EF8 EF9 EF10; do dotnet build MongoDB.EFCoreProvider.sln -c "Debug $cfg" || echo "BUILD FAILED: $cfg"; done
```

Expected: three successful builds. A compile error here is most likely `RequiredNavigationUnwindTests` referencing something the native branch renamed — fix by following the rename, not by deleting the test.

- [ ] **Step 6: Run all three majors and triage**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build 2>&1 | tee /tmp/ef370-port-$cfg.log | tail -30
done
```

Two failure families are **expected** here and are this task's real work (spec §2):

1. **The collection-Include case in `RequiredNavigationUnwindTests`.** Collection Include is already native on this branch (EF-339), so it takes the native path and its MQL baseline differs from the driver-LINQ one EF-370 pinned. Re-baseline it to the native shape and add a comment saying the divergence from #328 is expected and why.
2. **EF-367's strict `AssertTranslationFailed`** (234 call sites in the four Northwind Include suites) has never compiled against EF-370's changed row semantics. Any test that now succeeds where it previously threw must be re-baselined as a *pass*, not re-silenced.

For anything else, apply superpowers:systematic-debugging before changing a baseline. A row-count change is a bug until proven otherwise.

- [ ] **Step 7: Squash to one commit**

```bash
git reset --soft NativeQueryOngoing@{1}   # the tip before the cherry-pick; verify with git reflog first
git commit -m "EF-370: correct required-navigation \$unwind semantics and stop dropping composed operators (ported)

Ported from the main-bound branch (PR #328, 4a9ec84) because main is unavailable as an integration
point. Same two fixes: a required reference navigation emitted a left-outer \$unwind where the join's
own semantics call for an inner one, and composed Where/OrderBy/Skip/Take was silently discarded for a
multi-join Include (EF-369).

Native-branch-specific fallout, absent on main: the collection-Include case in
RequiredNavigationUnwindTests takes the NATIVE path here (collection Include went native in EF-339), so
its MQL baseline differs from the one #328 pinned against driver-LINQ."
```

- [ ] **Step 8: Verify the squash preserved the tree, then fast-forward the native branch**

```bash
git diff --stat EF-370-on-native-presquash-safety..HEAD   # informational: shows the port's own delta
git log --oneline -2
```

Expected: one new commit on top of the previous native tip.

- [ ] **Step 9: Push**

```bash
git push origin NativeQueryOngoing
```

---

### Task 2: Rebase EF-368 and measure the real baseline

Exit criteria are re-measured, never inherited (spec §7.1).

**Files:**
- Create: `docs/superpowers/plans/2026-08-04-native-reference-include-baseline.md` (the measurement record)

**Interfaces:**
- Consumes: Task 1's advanced `NativeQueryOngoing` tip.
- Produces: the measured in-scope case count, which becomes Task 8's exit criterion.

- [ ] **Step 1: Rebase EF-368 onto the ported tip**

```bash
git checkout EF-368
git rebase NativeQueryOngoing
git log --oneline -3
```

Expected: the two EF-368 design commits (`0ff0582`, `733f7f1`) plus the spike-findings commit sit on top of Task 1's commit. These are docs-only, so conflicts are unlikely; if `docs/failing-spec-tests.md` conflicts, keep both sides' rows as in Task 1 Step 4.

- [ ] **Step 2: Capture the pre-slice `NativeOnly` baseline**

```bash
MONGODB_URI= ATLAS_URI= dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindInclude|FullyQualifiedName~NorthwindEFPropertyInclude|FullyQualifiedName~NorthwindStringInclude|FullyQualifiedName~NorthwindIncludeNoTracking" \
  --logger "trx;LogFileName=/tmp/ef368-baseline-EF10.trx" 2>&1 | tail -20
```

- [ ] **Step 3: Extract the in-scope failing cases**

The seven in-scope methods (spec Appendix B of the spike) across the four Include classes, both `async` values:

```bash
python3 - <<'PY'
import re, xml.etree.ElementTree as ET
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tree=ET.parse('/tmp/ef368-baseline-EF10.trx')
methods=["Include_reference","Include_reference_alias_generation","Include_reference_with_filter",
         "Include_reference_with_filter_reordered","Include_reference_distinct_is_server_evaluated",
         "Include_reference_single_or_default_when_no_result","Include_empty_reference_sets_IsLoaded"]
rows=[(r.get('testName'), r.get('outcome')) for r in tree.iter('{%s}UnitTestResult'%ns['t'])]
hits=[(n,o) for n,o in rows if any(m in (n or '') for m in methods)]
print("in-scope cases found:", len(hits))
for n,o in sorted(hits): print(f"  {o:8} {n}")
PY
```

- [ ] **Step 4: Record the measurement**

Write `docs/superpowers/plans/2026-08-04-native-reference-include-baseline.md` containing: the exact command run, the EF major, the case count by outcome, and the sentence *"This is the exit criterion for Task 8; the spike's figure of 56 was measured at `365391f`, before EF-366, EF-367 and the EF-370 port, and is not used."*

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-08-04-native-reference-include-baseline.md
git commit -m "EF-368: record the re-measured pre-slice baseline on the ported base"
```

---

### Task 3: Prove the recognizer rejects a user join

The spike's disjointness claim is **measured false** (spec §5.1): a user join with a downstream `Include` *does* carry a trailing `IncludeExpression`, differing only by the double hop. This task pins that before any gate opens.

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`

**Interfaces:**
- Consumes: Task 1's `LookupExpression.PreserveNullAndEmptyArrays`.
- Produces: `internal static bool IsSingleLevelReferenceIncludeSelector(LambdaExpression selector)` on `MongoQueryableMethodTranslatingExpressionVisitor` — `internal` (not `private`) so the unit-test assembly can reach it via `InternalsVisibleTo`. Tasks 4–6 call it.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// The recognizer's single-hop conjunct is LOAD-BEARING, not defence-in-depth: a user-authored join with a
/// downstream Include DOES produce a trailing IncludeExpression, differing from EF's nav-expansion shape only
/// by a double hop (ti.Outer.Outer vs ti.Outer). A predicate matching merely Member.Name == "Outer" admits it,
/// because the OUTERMOST hop of ti.Outer.Outer is also named "Outer". See design §5.1.
/// </summary>
public class ReferenceIncludeRecognizerTests
{
    [Fact]
    public void Rejects_double_hop_entity_expression_from_a_user_join()
    {
        var selector = BuildIncludeSelector(doubleHop: true);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Accepts_single_hop_entity_expression_from_nav_expansion()
    {
        var selector = BuildIncludeSelector(doubleHop: false);

        Assert.True(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Rejects_a_collection_navigation()
    {
        var selector = BuildIncludeSelector(doubleHop: false, collectionNavigation: true);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Rejects_a_bare_parameter_body()
    {
        var param = Expression.Parameter(typeof(object), "ti");
        var selector = Expression.Lambda(param, param);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    // Builds the tree shape EF produces, without needing a live model: a TransparentIdentifier-typed
    // parameter, an IncludeExpression over either ti.Outer (nav-expansion) or ti.Outer.Outer (user join).
    private static LambdaExpression BuildIncludeSelector(bool doubleHop, bool collectionNavigation = false)
        => ReferenceIncludeTestTrees.Build(doubleHop, collectionNavigation);
}
```

`ReferenceIncludeTestTrees.Build` needs a real `INavigation`, so build it from a throwaway model in the same file using the existing unit-test model helpers. Look at how `MongoCardinalityRouteTests` in the same directory obtains an `IEntityType`; follow that pattern exactly rather than inventing a second one.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" \
  --filter "FullyQualifiedName~ReferenceIncludeRecognizerTests"
```

Expected: FAIL — `IsSingleLevelReferenceIncludeSelector` does not exist (compile error).

- [ ] **Step 3: Implement the recognizer**

Add next to `IsSingleLevelCollectionIncludeSelector` in `MongoQueryableMethodTranslatingExpressionVisitor.cs`, following that method's doc-comment style:

```csharp
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="selector"/> is the synthetic
    /// <c>Select(ti =&gt; Include(ti.Outer, Nav, ti.Inner))</c> EF's nav-expansion generates for a
    /// single-level, root-level REFERENCE <c>Include</c> (e.g. <c>Orders.Include(o =&gt; o.Customer)</c>).
    /// <para>
    /// The single-hop requirement — the <c>IncludeExpression</c>'s <see cref="IncludeExpression.EntityExpression"/>
    /// must be a member access whose own <c>Expression</c> IS the lambda parameter — is LOAD-BEARING, not
    /// defence-in-depth. A user-authored join with a downstream Include, e.g.
    /// <c>Orders.Join(Customers, o =&gt; o.CustomerId, c =&gt; c.Id, (o, c) =&gt; o).Include(o =&gt; o.Customer)</c>,
    /// DOES produce a trailing <c>IncludeExpression</c>; it differs from the nav-expansion shape only by a
    /// DOUBLE hop (<c>ti.Outer.Outer</c>) and by having two inner collections. Matching merely
    /// <c>Member.Name == "Outer"</c> would admit it, because the outermost hop of <c>ti.Outer.Outer</c> is also
    /// named <c>Outer</c> — and admitting it would change a user query's row semantics.
    /// </para>
    /// </summary>
    internal static bool IsSingleLevelReferenceIncludeSelector(LambdaExpression selector)
        => selector.Parameters.Count == 1
           && selector.Body is IncludeExpression { Navigation: INavigation navigation } include
           && !navigation.IsCollection
           && !navigation.IsEmbedded()
           && include.EntityExpression is MemberExpression { Member.Name: "Outer" } outerAccess
           && outerAccess.Expression == selector.Parameters[0]
           && selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" \
  --filter "FullyQualifiedName~ReferenceIncludeRecognizerTests"
```

Expected: 4 passed. No behaviour has changed yet — nothing calls the recognizer.

- [ ] **Step 5: Run on all three majors and commit**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug $cfg" \
    --filter "FullyQualifiedName~ReferenceIncludeRecognizerTests" || echo "FAILED: $cfg"
done
git add tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ReferenceIncludeRecognizerTests.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-368: add the reference-Include recognizer, pinning the single-hop conjunct

A user join with a downstream Include DOES carry a trailing IncludeExpression, differing only by the
double hop, so the single-hop check is load-bearing. Recognizer only — no call site yet."
```

---

### Task 4: Default-deny join admission

Spec §3.2. `_hasUnsupportedOperator` is documented "never unset", so the decision is computed from two provisional signals rather than toggled.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Consumes: `IsSingleLevelReferenceIncludeSelector` (Task 3).
- Produces: `internal void MarkSawCandidateReferenceIncludeJoin()`, `internal void MarkReferenceIncludeConfirmed()`, and a `Route` that yields `NativeRoute.Fallback` while a candidate is unconfirmed. Task 5 calls `MarkReferenceIncludeConfirmed`.

- [ ] **Step 1: Write the failing test — a user join must still fall back**

```csharp
// tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
[Fact]
public void User_join_is_not_admitted_by_the_candidate_join_signal()
{
    using var db = SingleReferenceContext();

    // A user join with NO Include: nothing ever confirms, so the candidate signal alone must not
    // make this native. NativeOnly forbids the fallback, so a decline surfaces as a throw.
    var query = db.Orders
        .Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o)
        .AsNativeOnly();

    Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
}
```

Use whatever helper the neighbouring `Native*Tests` files use to select `MongoQueryMode.NativeOnly` (grep for `MongoQueryMode.NativeOnly` in `tests/.../FunctionalTests/Query/` and copy the established idiom; do not add a new one).

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests.User_join_is_not_admitted"
```

Expected: FAIL (compile error — the test class and context helper do not exist yet). Write the minimal fixture to make it compile and fail for the right reason, then continue.

- [ ] **Step 3: Add the two provisional signals**

In `MongoSelectDefinition.cs`, immediately after the `_hasUnsupportedOperator` region:

```csharp
    // ── Reference-Include candidate join (EF-368) ──────────────────────────────────
    //
    // PopulateNativeSlots visits the JOIN node before the trailing Select that identifies a reference
    // Include, so the gate has to decide on the join before the IncludeExpression has been seen. Because
    // _hasUnsupportedOperator is never unset (by design), "mark non-native at the join then un-mark at the
    // Select" is not available. Instead the two signals below are recorded and Route COMPUTES the decision,
    // the same way UsesDriverJoinFields computes the document shape rather than tracking it as mutable state.
    //
    // This is DEFAULT-DENY: a user join with no trailing Include, or one whose Include fails any recognizer
    // conjunct, is never confirmed and therefore routes to Fallback.

    private bool _sawCandidateReferenceIncludeJoin;
    private bool _referenceIncludeConfirmed;

    /// <summary>
    /// Records that a <c>Join</c>/<c>LeftJoin</c>/<c>GroupJoin</c> was seen which MIGHT be EF's
    /// nav-expansion of a single-level reference <c>Include</c>. Does not admit anything on its own.
    /// </summary>
    internal void MarkSawCandidateReferenceIncludeJoin()
        => _sawCandidateReferenceIncludeJoin = true;

    /// <summary>
    /// Records that the trailing <c>Select</c> was recognized as a single-level reference <c>Include</c>
    /// (see <c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector</c>),
    /// confirming the candidate join recorded by <see cref="MarkSawCandidateReferenceIncludeJoin"/>.
    /// </summary>
    internal void MarkReferenceIncludeConfirmed()
        => _referenceIncludeConfirmed = true;

    /// <summary>
    /// A candidate join that no trailing Include confirmed — the query must fall back.
    /// </summary>
    internal bool HasUnconfirmedCandidateJoin
        => _sawCandidateReferenceIncludeJoin && !_referenceIncludeConfirmed;
```

- [ ] **Step 4: Add the `Route` term**

Change `Route`'s first condition so an unconfirmed candidate forces `Fallback`:

```csharp
    internal NativeRoute Route
        => _hasUnsupportedOperator || HasUnconfirmedCandidateJoin ? NativeRoute.Fallback
```

Leave the rest of the expression exactly as it is, and extend the existing XML doc with one sentence: *"An unconfirmed reference-Include candidate join (EF-368) also forces Fallback — see `HasUnconfirmedCandidateJoin`."*

- [ ] **Step 5: Record the candidate in the slot populator**

In `NativeSlotPopulator.cs`, add an arm **before** the `else if (!IsNativeRepresentableSlotOperator(...))` catch-all:

```csharp
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#endif
                )
        {
            // EF-368: might be EF's nav-expansion of a single-level reference Include. Record a candidate
            // rather than marking non-native; TranslateSelect confirms it when the trailing
            // IncludeExpression matches the recognizer. Unconfirmed candidates route to Fallback, so this
            // is default-deny and a user join is unaffected. See MongoSelectDefinition §Reference-Include
            // candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
```

Note the `#if !EF8 && !EF9` on `LeftJoin` only: on EF8/EF9 the optional-FK case uses EF's *internal* `LeftJoin`, which never reaches here, and the asymmetry is deliberate (spec §4). Do **not** add `QueryableMethods.LeftJoin` unconditionally — it does not exist on those targets.

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests.User_join_is_not_admitted"
```

Expected: PASS.

- [ ] **Step 7: Run the full functional + spec suites on all three majors**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build 2>&1 | tail -8
done
```

Expected: **zero new failures**. Nothing is confirmed yet, so every join still routes to `Fallback` exactly as before — this step proves the refactor is behaviour-neutral. Any change here means the candidate arm is swallowing a case the catch-all used to mark; fix it before continuing.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-368: admit candidate joins by computed default-deny, not by un-marking

PopulateNativeSlots sees the join before the Select that identifies a reference Include, and
_hasUnsupportedOperator is never unset. So record a candidate at the join, confirm at the Select, and
compute Fallback for an unconfirmed candidate - the same compute-from-state idiom as
UsesDriverJoinFields. Behaviour-neutral: nothing confirms yet."
```

---

### Task 5: Confirm the shape and register the lookup

The slice's actual capability.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Consumes: `IsSingleLevelReferenceIncludeSelector` (Task 3), `MarkReferenceIncludeConfirmed` (Task 4), `LookupExpression.PreserveNullAndEmptyArrays` (Task 1).
- Produces: native execution for the recognized shape; the `_lookup_<Nav>` document shape in both modes.

- [ ] **Step 1: Write the failing tests — native MQL, both unwind semantics**

```csharp
[Fact]
public void Required_reference_Include_goes_native_with_an_inner_unwind()
{
    using var db = SingleReferenceContext();

    var results = db.Orders.Include(o => o.Buyer).AsNativeOnly().ToList();

    // O3's buyer is dangling and the navigation is REQUIRED, so the inner $unwind drops it.
    Assert.Equal(3, results.Count);
    Assert.All(results, o => Assert.NotNull(o.Buyer));
    AssertMql(db, """{ "$lookup" : { "from" : "<buyers>", "localField" : "buyer_id", "foreignField" : "_id", "as" : "_lookup_Buyer" } }, { "$unwind" : { "path" : "$_lookup_Buyer", "preserveNullAndEmptyArrays" : false } }""");
}

[Fact]
public void Composed_Where_stays_ahead_of_the_lookup()
{
    using var db = SingleReferenceContext();

    var results = db.Orders.Where(o => o.Total > 10).Include(o => o.Buyer).AsNativeOnly().ToList();

    Assert.NotEmpty(results);
    // $match BEFORE $lookup: filter/sort/paging push ahead of the join (design §6).
    AssertMql(db, """{ "$match" : { "total" : { "$gt" : 10 } } }, { "$lookup" : ...""");
}
```

Replace `<buyers>` and the MQL text with the actual emitted pipeline: run the test once, read the logged MQL from the failure output, and paste it verbatim. Do **not** guess element names — they come from the fixture's mapping. Follow the `AssertMql` idiom already used by the neighbouring `Native*Tests` classes.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```

Expected: FAIL with `NativeTranslationNotSupportedException` — the candidate join is still unconfirmed.

- [ ] **Step 3: Confirm and register in `TranslateSelect`**

In `TranslateSelect`, add a branch **before** the existing `if (IsSingleLevelCollectionIncludeSelector(selector) && ...HasTerminalOperator)`:

```csharp
        if (IsSingleLevelReferenceIncludeSelector(selector))
        {
            if (!TryConfirmReferenceInclude(mongoQueryExpression, selector))
            {
                // Recognized the SHAPE but declined the case (second reference Include, composite key,
                // post-terminal, …). The candidate join stays unconfirmed, so Route computes Fallback.
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (IsSingleLevelCollectionIncludeSelector(selector) && mongoQueryExpression.Select.HasTerminalOperator)
```

and add the helper next to the recognizer:

```csharp
    /// <summary>
    /// Registers the forced-unwind reference <c>$lookup</c> for a recognized single-level reference
    /// <c>Include</c> and confirms the candidate join, or returns <see langword="false"/> to decline.
    /// <para>
    /// Registering the lookup makes <see cref="MongoQueryExpression.UsesDriverJoinFields"/> compute
    /// <see langword="false"/>, so the native lowerer, the DOM shaper and the driver-LINQ
    /// <c>StripJoinForLookup</c> fallback all agree on the <c>_lookup_&lt;Nav&gt;</c> field — which is why
    /// the shaper is correct whichever way the gate later decides (design §6.1).
    /// </para>
    /// </summary>
    private static bool TryConfirmReferenceInclude(
        MongoQueryExpression mongoQueryExpression,
        LambdaExpression selector)
    {
        var navigation = (INavigation)((IncludeExpression)selector.Body).Navigation;

        // Declines, each with a tripwire test (design §5.3).
        if (mongoQueryExpression.Select.HasTerminalOperator                       // composed after a terminal
            || mongoQueryExpression.InnerCollections.Count != 1                   // sibling Include / user double-join
            || mongoQueryExpression.GetPendingLookups().Any(l => l.ForceUnwind)   // second reference Include, incl. same-target
            || navigation.ForeignKey.Properties.Count != 1                        // composite FK
            || navigation.ForeignKey.PrincipalKey.Properties.Count != 1)          // composite PK
        {
            return false;
        }

        var lookup = new Expressions.LookupExpression(navigation, forceUnwind: true)
        {
            // Inner for a required navigation, left-outer for an optional one - EF-370's discriminator.
            PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired
        };

        // A ThenInclude/transitive hop rewrites LocalField to "_lookup_<through>.<field>", which is out of
        // scope for a single-level slice and is exactly what IsStreamableReference rejects.
        if (lookup.LocalField.StartsWith(Expressions.LookupExpression.LookupAliasPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        mongoQueryExpression.AddLookup(lookup);
        mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
        return true;
    }
```

Note on the discriminator: at this site the LINQ operator is no longer in hand (the join was translated earlier), so this uses `ForeignKey.IsRequired` — which is EF-370's documented fallback for exactly this situation, and matches the retroactive-flattening site in `TranslateJoinCore`. If a test shows the two disagree for a shape in scope, thread the operator through from the join instead of changing this to a guess.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```

Expected: PASS, with the pinned MQL matching.

- [ ] **Step 5: Add the optional-FK case, gated**

```csharp
#if !EF8 && !EF9
    [Fact]
    public void Optional_reference_Include_goes_native_with_a_left_outer_unwind()
    {
        using var db = OptionalReferenceContext();

        var results = db.Orders.Include(o => o.Carrier).AsNativeOnly().ToList();

        // Left-outer: rows with no FK and rows with a DANGLING FK both survive, navigation null.
        Assert.Equal(4, results.Count);
        Assert.Contains(results, o => o.Carrier == null);
        AssertMql(db, """..., { "$unwind" : { "path" : "$_lookup_Carrier", "preserveNullAndEmptyArrays" : true } }""");
    }
#endif
```

The `#if` is on the **optional-FK** case only, because EF lowers an optional navigation to `LeftJoin`, which is EF-internal on EF8/EF9 and never reaches the provider. Required-navigation tests above stay ungated — a required nav lowers to `Queryable.Join`, which dispatches on all three majors.

- [ ] **Step 6: Run all three majors**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build 2>&1 | tail -8
done
```

Expected: the four Northwind Include suites now show **newly passing** cases (the movers) and **newly failing MQL assertions** (the re-baselines). Both are expected; Task 8 handles them. Do not re-baseline anything yet — a row-count change is a bug until proven otherwise, and mixing the two makes that impossible to see.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-368: single-level reference Include goes native

Register a forced-unwind reference lookup for the recognized shape so UsesDriverJoinFields computes
false and the lowerer, the DOM shaper and the driver-LINQ fallback all agree on _lookup_<Nav>. The
\$unwind follows the navigation's requiredness, so a dangling required FK drops the row as it does on
the fallback path. Spec baselines not yet touched."
```

---

### Task 6: Declines with tripwires

Every decline must be a decline, not a silent pass (spec §5.3).

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Consumes: everything from Task 5.
- Produces: no production change expected. If a tripwire fails, the fix belongs in `TryConfirmReferenceInclude`.

- [ ] **Step 1: Write one tripwire per decline**

Each asserts `NativeOnly` **throws** (proving the decline) and that `Native` returns the **same rows** as `DriverLinq` (proving the fallback is correct):

```csharp
public static TheoryData<string, Func<ReferenceIncludeDbContext, IQueryable>> DeclinedShapes => new()
{
    { "sibling reference Includes",   db => db.Lines.Include(l => l.Order).Include(l => l.Product) },
    { "same-target sibling Includes", db => db.Docs.Include(d => d.Author).Include(d => d.Editor) },
    { "ThenInclude / transitive",     db => db.Lines.Include(l => l.Order).ThenInclude(o => o.Buyer) },
    { "composite FK",                 db => db.CompositeLines.Include(l => l.Order) },
    { "after a terminal",             db => db.Orders.Distinct().Include(o => o.Buyer) },
    { "reference + collection",       db => db.Orders.Include(o => o.Buyer).Include(o => o.Lines) },
    // THE LOAD-BEARING ONE. A user-authored join with a downstream Include produces a trailing
    // IncludeExpression whose EntityExpression is ti.Outer.Outer - a DOUBLE hop. Task 3's unit tests
    // prove the single-hop conjunct rejects a hand-built double-hop tree; this row is the only test
    // that feeds the predicate a REAL EF-compiled tree for that shape. Without it, the conjunct is
    // verified against IR the implementer wrote rather than against what EF actually emits.
    { "user join with downstream Include",
      db => db.Orders.Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o).Include(o => o.Buyer) },
};

[Theory]
[MemberData(nameof(DeclinedShapes))]
public void Declined_shapes_throw_under_NativeOnly_and_match_DriverLinq_under_Native(
    string description,
    Func<ReferenceIncludeDbContext, IQueryable> build)
{
    using var nativeOnly = SingleReferenceContext(MongoQueryMode.NativeOnly);
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => build(nativeOnly).Cast<object>().ToList());

    using var native = SingleReferenceContext(MongoQueryMode.Native);
    using var driverLinq = SingleReferenceContext(MongoQueryMode.DriverLinq);
    Assert.Equal(
        build(driverLinq).Cast<object>().Count(),
        build(native).Cast<object>().Count());
}
```

The `same-target sibling Includes` row is the one the spike's `InnerCollections.Count > 1` guard would have let through, because `_innerCollections` is keyed by entity type (spec §5.2). Keep it even if it looks redundant with the plain sibling row — it is testing a different guard.

- [ ] **Step 2: Run and expect some to fail**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" \
  --filter "FullyQualifiedName~Declined_shapes_throw"
```

Any row that does **not** throw under `NativeOnly` is a hole in `TryConfirmReferenceInclude`. Fix the guard, not the test.

- [ ] **Step 3: Make them all pass, then run three majors**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug $cfg" \
    --filter "FullyQualifiedName~NativeReferenceIncludeTests" || echo "FAILED: $cfg"
done
```

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-368: tripwire every decline, including the same-target sibling case

Each declined shape must throw under NativeOnly and return DriverLinq's rows under Native. The
same-target sibling row is what the spike's InnerCollections.Count guard would have admitted, since
_innerCollections is keyed by entity type."
```

---

### Task 7: Differential tests and both materializers

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/RequiredNavigationUnwindTests.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Consumes: the dangling-FK seed from Task 1's ported fixture; native execution from Task 5.
- Produces: the slice's correctness gate.

- [ ] **Step 1: Add mode-differential assertions to the ported fixture**

`RequiredNavigationUnwindTests` already seeds dangling FKs (Buyer/Order/Product/Carrier/Line, with never-inserted targets). It asserts row semantics but not mode. Add, for each existing required-navigation case, an assertion that all three modes agree:

```csharp
    // EF-368: the same seed, now asserted ACROSS MODES. This is the assertion that would have caught
    // EF-370's row-count divergence, and it is the slice's gate: the native pipeline and the driver-LINQ
    // fallback must return the same rows over a dangling foreign key, not merely both "work".
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Required_single_reference_Include_excludes_dangling_foreign_key_in_every_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var orders = db.Orders.Include(o => o.Buyer).OrderBy(o => o.Id).ToList();

        // O3's buyer_id is dangling and Buyer is required => inner join => O3 absent, in EVERY mode.
        Assert.Equal(3, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Buyer));
        Assert.DoesNotContain(orders, o => o.Buyer == null);
    }
```

- [ ] **Step 2: Add the streaming-materializer case**

Spec §6.3: on a **unidirectional** model (no inverse collection on the reference target) `StreamingEligibility.IsEligible` is true, so the one-pass streaming materializer fires for this shape via `LookupReferencePlan` — a different code path from the DOM shaper that Northwind-shaped models use.

```csharp
    /// <summary>
    /// Reference Include on a UNIDIRECTIONAL model: StreamingEligibility admits the root (no inverse
    /// collection on the target), so this exercises the one-pass STREAMING materializer's
    /// LookupReferencePlan rather than the DOM shaper. Both read _lookup_&lt;Nav&gt;; asserting only the
    /// bidirectional case would leave streaming untested while looking covered. See design §6.3.
    /// </summary>
    [Fact]
    public void Reference_Include_on_a_unidirectional_model_uses_the_streaming_materializer()
    {
        using var db = UnidirectionalContext();

        var orders = db.UniOrders.Include(o => o.UniCustomer).AsNativeOnly().ToList();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.NotNull(o.UniCustomer));
    }
```

Add an explicit assertion that the streaming path was actually taken, using whichever mechanism the existing streaming tests use (grep for `StreamingEligibility` or `IsEligible` under `tests/.../FunctionalTests/Query/`). A test that merely returns correct rows cannot distinguish the two materializers, which is precisely the gap this test exists to close.

- [ ] **Step 3: Run all three majors**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug $cfg" \
    --filter "FullyQualifiedName~RequiredNavigationUnwindTests|FullyQualifiedName~NativeReferenceIncludeTests" \
    || echo "FAILED: $cfg"
done
```

Expected: all pass on all three. The required-navigation differential cases are **ungated** — if they fail to compile on EF8/EF9 the cause is a fixture reference, not a translation limit; fix the reference rather than adding an `#if`.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/RequiredNavigationUnwindTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-368: assert Native == DriverLinq == NativeOnly over the dangling-FK seed

Extends EF-370's fixture with mode-differential assertions - the gate for this slice - and covers the
STREAMING materializer on a unidirectional model, which StreamingEligibility admits and which the
Northwind-shaped DOM cases would otherwise leave untested."
```

---

### Task 8: Re-baseline, docs, and exit criteria

**Files:**
- Modify: `tests/.../SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs`, `NorthwindEFPropertyIncludeQueryMongoTest.cs`, `NorthwindStringIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`
- Modify: `tests/.../FunctionalTests/Query/QueryModeGateIncludeTests.cs`
- Modify: `docs/failing-spec-tests.md`, `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: Task 2's measured baseline; the capability from Tasks 5–7.
- Produces: a green three-major tree.

- [ ] **Step 1: Separate movers from re-baselines**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build \
    --logger "trx;LogFileName=/tmp/ef368-post-$cfg.trx" 2>&1 | tail -8
done
```

Classify **every** failure before changing anything:
- **MQL-assertion failure only** → a re-baseline. The pipeline changed from `_outer`/`_inner` to flat `_lookup_*`, which is expected and is not a break.
- **Row count, entity identity, or thrown-type change** → a bug. Apply superpowers:systematic-debugging. Do not re-baseline it.

- [ ] **Step 2: Re-baseline the MQL assertions**

Use the repo's baseline-rewrite mechanism rather than editing 56 strings by hand:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindInclude|FullyQualifiedName~NorthwindEFPropertyInclude|FullyQualifiedName~NorthwindStringInclude|FullyQualifiedName~NorthwindIncludeNoTracking"
git diff --stat
```

Then **read the diff**. Any hunk that changes something other than the expected `_outer`/`_inner` → `_lookup_*` shape or the `preserveNullAndEmptyArrays` value is a bug the rewrite has just silently accepted; revert that hunk and debug it.

- [ ] **Step 3: Update `QueryModeGateIncludeTests`**

`Reference_include_falls_back_to_driver_linq_under_Native_mode` asserts the old `_outer`/`_inner` fallback. Reference Include no longer falls back, so **rename the test to state the new behaviour** and assert the native shape. Do not delete it — it is the gate's regression test.

- [ ] **Step 4: Check the exit criterion against Task 2's measurement**

**Two corrections carried forward from Task 2 — both were defects in this plan, verified during that task. Do not repeat them:**

1. **`NativeOnly` measurement requires `MONGODB_EF_NATIVE_ONLY=1`.** Without it the driver-LINQ fallback masks every native gap and the run reports 0 failures — it measures nothing. The env var flips every spec context to `MongoQueryMode.NativeOnly` via `MongoTestStore.cs:44`.
2. **Match method names EXACTLY, never by substring.** `"Include_reference"` as a substring also matches `Include_reference_GroupBy_Select`, `Include_reference_Join_GroupBy_Select`, and `Include_reference_SelectMany_GroupBy_Select`, all out of scope — that inflated the count from 56 to 192 when first attempted.

The measured baseline is **56 in-scope cases failing, 0 passing** (EF10, at the ported base). Compare the newly-passing in-scope cases against that, and against the record in `docs/superpowers/plans/2026-08-04-native-reference-include-baseline.md`. Every mover must be accounted for. A mover you cannot explain is a bug, and a case you expected to move that did not is a missing capability — report both rather than adjusting the number.

- [ ] **Step 5: Update `docs/failing-spec-tests.md`**

Restate **only** the rows this slice moves. Do not touch the totals column — its basis is lost and it reconciles under no rule (spec §7.1). Add one line explaining why the totals were left alone, referencing this slice.

- [ ] **Step 6: Fold in the two doc corrections**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`:
1. Reference `Include` nav-expands to `Queryable.Join` for a **required** FK and `LeftJoin` only for an **optional** one — not uniformly `LeftJoin`. That distinction is the source of the inner-vs-left `$unwind` semantics, so the imprecision is not cosmetic.
2. Update the Include section: single-level reference `Include` is now **native**; still deferred are `ThenInclude`/multi-level, sibling reference Includes, filtered Include, and nav-expansion `LeftJoin` generally.

And in `MongoQueryableMethodTranslatingExpressionVisitor.cs`, fix `IsTransparentIdentifierMemberAccessSelector`'s XML doc: it claims `TranslateJoinCore` unconditionally marks the outer side non-native. It does not — `NativeSlotPopulator`'s catch-all does. Check first whether the EF-370 port already corrected this; if so, skip.

- [ ] **Step 7: Final verification — all three majors green**

```bash
for cfg in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build 2>&1 | tail -8
done
```

Expected: **zero failures on all three.** Report the actual numbers; do not claim green without this output.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "EF-368: re-baseline the reference-Include spec suites and fold in the doc corrections

The fallback pipeline for a lone reference Include changed from _outer/_inner to the flat _lookup_*
shape, which per the versioning rubric is not a break. Movers verified against the baseline measured on
the ported base, not the spike's pre-EF-366/367/370 figure. failing-spec-tests.md restates only this
slice's rows; the totals column reconciles under no rule and is deliberately untouched."
```

- [ ] **Step 9: Squash the slice and push**

```bash
git branch EF-368-presquash                      # required by the stacked-PR convention
git reset --soft NativeQueryOngoing
git commit    # one squashed commit, message starting "EF-368: "
git diff --quiet EF-368-presquash HEAD && echo "TREE IDENTICAL" || echo "TREE DIFFERS - investigate"
git push --force-with-lease origin EF-368
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §1 blocking unknown re-answered | context for Tasks 4–5; no code |
| §2 prerequisite port | Task 1 |
| §3 architecture / one state change | Task 5 |
| §3.1 registration site (`TranslateSelect`) | Task 5 Step 3 |
| §3.2 default-deny candidate join | Task 4 |
| §4 scope decisions | Tasks 5 (opt-FK `#if`), 6 (siblings declined) |
| §5 recognizer | Task 3 |
| §5.1 single-hop conjunct load-bearing | Task 3 Steps 1, 3 |
| §5.2 sibling guard correction | Task 5 Step 3, Task 6 Step 1 (same-target row) |
| §5.3 decline list | Task 6 |
| §6 data flow / MQL | Task 5 Steps 1, 5 |
| §6.1 safety property | Task 5 Step 3 (doc comment) |
| §6.2 hazards neutralized | Task 7 Step 1 |
| §6.3 both materializers | Task 7 Step 2 |
| §7 testing | Tasks 6, 7 |
| §7.1 re-measured exit criteria | Tasks 2, 8 Step 4 |
| §9 doc corrections | Task 8 Step 6 |

**Placeholder scan:** two deliberate lookup-it-up instructions remain, both where guessing would be worse than reading the codebase — the `AsNativeOnly`/mode-selection idiom (Task 4 Step 1, Task 5) and the streaming-path assertion mechanism (Task 7 Step 2). Both name the grep to run. MQL strings in Task 5 are marked to be pasted from actual output rather than guessed, because element names come from fixture mapping.

**Type consistency:** `IsSingleLevelReferenceIncludeSelector` (Task 3) is `internal static` and called in Tasks 5–6. `MarkSawCandidateReferenceIncludeJoin` / `MarkReferenceIncludeConfirmed` / `HasUnconfirmedCandidateJoin` (Task 4) are used in Task 4 Step 4 and Task 5 Step 3. `TryConfirmReferenceInclude` (Task 5) is the only caller of `MarkReferenceIncludeConfirmed`. `LookupExpression.PreserveNullAndEmptyArrays` (Task 1) is consumed in Task 5. `GetPendingLookups()` is the accessor name on this branch — **not** `Lookups`, which is the synthesized reference-lookup property.
