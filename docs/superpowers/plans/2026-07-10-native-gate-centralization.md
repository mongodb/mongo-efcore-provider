# Native Gate Centralization (EF-334) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold the three is-native gate signals (`Route` + vector-search + GroupBy-unsafe) behind one `NativeDisposition` classification that every gate site consults, with zero behavior change.

**Architecture:** Add a pure `internal static` classification function `ClassifyNativeDisposition(route, isGroupByFallbackUnsafe, containsVectorSearch, mode) -> NativeDisposition {Native, Fallback, HardDecline}` on `MongoShapedQueryCompilingExpressionVisitor`, plus a thin private instance wrapper that gathers those inputs from a `MongoQueryExpression`. Route the two gate sites that combine these signals through the wrapper. `Route` keeps its meaning (slot/projection representability); the streaming-eligibility predicate (`AllPendingLookupsAreStreamable`) is a separate axis and is untouched.

**Tech Stack:** C# / EF Core provider, xUnit (plain `Assert.*`, no FluentAssertions). Multi-EF via `Debug EF8|EF9|EF10` build configurations.

## Global Constraints

- **No behavior change.** Emitted MQL and throw/fallback behavior identical to the `5d53633` baseline (EF-347 tip). Verified byte-identical, same bar as EF-330/EF-332.
- **`<Nullable>enable</Nullable>`** on `src/` — annotate new members accordingly.
- **Preserve file BOMs.**
- **`internal` visibility is not a public-API break** — the new members are `internal`/`private`, invisible to consumers, so this is not a versioned change.
- **Single production file** touched: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`.
- Tests run **serially**; run one EF config at a time or via `/test-all`.
- Namespaces: `NativeRoute` and `MongoSelectDefinition` live in `MongoDB.EntityFrameworkCore.Query.Expressions`; `MongoQueryMode` in `MongoDB.EntityFrameworkCore.Infrastructure`; the gate + new `NativeDisposition` in `MongoDB.EntityFrameworkCore.Query.Visitors`.

---

### Task 1: Pure `ClassifyNativeDisposition` classification + unit tests

Introduce the `NativeDisposition` enum and the pure classification function, fully unit-tested over primitive inputs (no `MongoQueryExpression` fixture needed). Nothing consumes it yet — this task delivers a correct, tested pure function.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (add enum + method; no call-site changes yet)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` (create)

**Interfaces:**
- Produces:
  - `internal enum NativeDisposition { Native, Fallback, HardDecline }` (top-level, namespace `MongoDB.EntityFrameworkCore.Query.Visitors`)
  - `internal static NativeDisposition MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(NativeRoute route, bool isGroupByFallbackUnsafe, bool containsVectorSearch, MongoQueryMode mode)`

- [ ] **Step 1: Write the failing tests**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` (preserve the license header BOM by copying it from a sibling test file such as `MongoCardinalityRouteTests.cs`):

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class NativeDispositionTests
{
    private static NativeDisposition Classify(
        NativeRoute route,
        bool isGroupByFallbackUnsafe = false,
        bool containsVectorSearch = false,
        MongoQueryMode mode = MongoQueryMode.Native)
        => MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(
            route, isGroupByFallbackUnsafe, containsVectorSearch, mode);

    [Fact]
    public void WholeEntity_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.WholeEntity));

    [Fact]
    public void Projection_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.Projection));

    [Fact]
    public void GroupBy_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.GroupBy));

    [Fact]
    public void ScalarAggregate_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.ScalarAggregate));

    [Fact]
    public void Fallback_route_is_fallback()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.Fallback));

    [Fact]
    public void Vector_search_is_fallback_even_when_route_is_native()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.WholeEntity, containsVectorSearch: true));

    [Fact]
    public void GroupBy_unsafe_is_hard_decline_under_native()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.Native));

    [Fact]
    public void GroupBy_unsafe_is_hard_decline_under_native_only()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.NativeOnly));

    [Fact]
    public void GroupBy_unsafe_is_not_hard_decline_under_driver_linq()
        => Assert.NotEqual(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.DriverLinq));

    [Fact]
    public void Hard_decline_takes_precedence_over_vector_search()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, containsVectorSearch: true,
                mode: MongoQueryMode.Native));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeDispositionTests"
```
Expected: BUILD FAILURE / compile error — `NativeDisposition` and `ClassifyNativeDisposition` do not exist yet.

- [ ] **Step 3: Add the enum and pure classification function**

In `MongoShapedQueryCompilingExpressionVisitor.cs`, add the enum at the top of the namespace body (top-level, alongside the class — NOT nested private, so tests can reference it). Place it immediately after the `using` block, before the `internal ... class MongoShapedQueryCompilingExpressionVisitor` declaration:

```csharp
/// <summary>
/// The native-execution disposition of a query at the compile-time gate: whether it builds a native
/// pipeline, falls back to driver-LINQ gracefully (throwing only under <see cref="MongoQueryMode.NativeOnly"/>),
/// or must hard-decline (throw under <see cref="MongoQueryMode.Native"/> too, because its driver-LINQ
/// fallback returns silently wrong data). This is the single is-native classification the gate consults;
/// it is a superset of <see cref="Expressions.NativeRoute"/> (which answers "which native shape / is it slot
/// representable"), layering on the two is-native signals that are not slot representability: a lifted-out
/// vector search, and the GroupBy+Join wrong-data decline. Streaming-vs-DOM is a SEPARATE axis
/// (<c>AllPendingLookupsAreStreamable</c>) and is not part of this classification.
/// </summary>
internal enum NativeDisposition
{
    /// <summary>Build a native pipeline (via the <see cref="Expressions.NativeRoute"/>-appropriate builder).</summary>
    Native,

    /// <summary>Not natively representable: fall back to driver-LINQ; throw only under <see cref="MongoQueryMode.NativeOnly"/>.</summary>
    Fallback,

    /// <summary>Must throw under <see cref="MongoQueryMode.Native"/> AND <see cref="MongoQueryMode.NativeOnly"/>: the driver-LINQ fallback is wrong-data (GroupBy+Join).</summary>
    HardDecline
}
```

Then add the pure classification method to the class (place it next to `ContainsVectorSearch`, near the other gate helpers):

```csharp
/// <summary>
/// Classify a query's native disposition from the three authoritative is-native signals, read here in one
/// place. This is the single source of truth for the is-native gate decision (EF-334); all gate sites
/// consult it rather than re-deriving. Pure over its inputs so it is unit-testable in isolation.
/// </summary>
/// <param name="route">The slot/projection representability route (<see cref="MongoSelectDefinition.Route"/>).</param>
/// <param name="isGroupByFallbackUnsafe">Whether this is a GroupBy+Join whose driver-LINQ fallback is wrong-data (<see cref="MongoSelectDefinition.IsGroupByFallbackUnsafe"/>).</param>
/// <param name="containsVectorSearch">Whether the captured chain contains a lifted-out <c>VectorSearch</c> (<see cref="ContainsVectorSearch"/>).</param>
/// <param name="mode">The active <see cref="MongoQueryMode"/>.</param>
internal static NativeDisposition ClassifyNativeDisposition(
    NativeRoute route,
    bool isGroupByFallbackUnsafe,
    bool containsVectorSearch,
    MongoQueryMode mode)
{
    // GroupBy+Join is unsafe to fall back (driver-LINQ returns silently wrong data), so it hard-declines
    // under Native/NativeOnly. Explicit DriverLinq is the user's opt-in and runs it (not a hard decline).
    // Checked first so it takes precedence over the graceful-fallback signals.
    if (mode != MongoQueryMode.DriverLinq && isGroupByFallbackUnsafe)
    {
        return NativeDisposition.HardDecline;
    }

    // Not natively representable (an unsupported slot/projection shape, or a lifted-out vector search the
    // native lowerer never sees) -> graceful driver-LINQ fallback.
    if (route == NativeRoute.Fallback || containsVectorSearch)
    {
        return NativeDisposition.Fallback;
    }

    return NativeDisposition.Native;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeDispositionTests"
```
Expected: PASS (10 tests passed).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeDispositionTests.cs
git commit -m "EF-334: Add NativeDisposition classification (pure fn + unit tests)"
```

---

### Task 2: Route the two combining gate sites through the classification

Wire the classification into the two gate sites that today combine these signals, preserving each site's exact effective condition. Behavior-preserving; the deliverable is "both sites use the classification and every existing gate/unit test stays green."

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
  - `VisitShapedQuery` (the GroupBy+Join hard-decline block, currently ~line 145)
  - `TryBuildNativeFactory` (the native-vs-driver block, currently ~line 487)
  - add the private instance wrapper

**Interfaces:**
- Consumes: `NativeDisposition`, `ClassifyNativeDisposition(NativeRoute, bool, bool, MongoQueryMode)` from Task 1; existing `ContainsVectorSearch(Expression?)`.
- Produces: `private NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)` (instance wrapper).

- [ ] **Step 1: Add the private instance wrapper**

Add next to the pure method:

```csharp
/// <summary>
/// Gather the three is-native signals from <paramref name="q"/> and classify (see the pure
/// <see cref="ClassifyNativeDisposition(NativeRoute, bool, bool, MongoQueryMode)"/> overload). The one
/// signal that cannot live on <see cref="MongoSelectDefinition"/> is vector search: the <c>VectorSearch</c>
/// call is lifted out of the tree before the Select is built, so it is read from the captured chain here.
/// </summary>
private NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)
    => ClassifyNativeDisposition(
        q.Select.Route,
        q.Select.IsGroupByFallbackUnsafe,
        ContainsVectorSearch(q.CapturedExpression),
        mode);
```

- [ ] **Step 2: Rewrite the hard-decline site in `VisitShapedQuery`**

Find the current block (near the top of `VisitShapedQuery`):

```csharp
        if (((MongoQueryCompilationContext)QueryCompilationContext).QueryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.IsGroupByFallbackUnsafe)
        {
            throw new NativeTranslationNotSupportedException(
                "Query combines GroupBy with a Join, which the native translator does not support and whose "
                + "driver-LINQ fallback returns incorrect results; use MongoQueryMode.DriverLinq to opt in to "
                + "the driver-LINQ execution of this query.");
        }
```

Replace it with (introduce the `mode` local, classify, act only on `HardDecline`):

```csharp
        // The is-native disposition is centralized in ClassifyNativeDisposition (EF-334). Here we act only on
        // HardDecline: a GroupBy+Join whose driver-LINQ fallback returns silently wrong data must throw under
        // Native/NativeOnly rather than route to that fallback (explicit DriverLinq stays the user's opt-in).
        var mode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) == NativeDisposition.HardDecline)
        {
            throw new NativeTranslationNotSupportedException(
                "Query combines GroupBy with a Join, which the native translator does not support and whose "
                + "driver-LINQ fallback returns incorrect results; use MongoQueryMode.DriverLinq to opt in to "
                + "the driver-LINQ execution of this query.");
        }
```

Note: `ClassifyNativeDisposition` computes `ContainsVectorSearch` here too, but the result is not consulted for the `HardDecline` decision — HardDecline is decided before the vector-search branch in the pure method. This is a harmless extra chain-scan, not a behavior change.

- [ ] **Step 3: Rewrite the native-vs-driver site in `TryBuildNativeFactory`**

Find the current block (near the end of `TryBuildNativeFactory`):

```csharp
        if (mongoQueryExpression.Select.Route is NativeRoute.Fallback or NativeRoute.ScalarAggregate
            || ContainsVectorSearch(mongoQueryExpression.CapturedExpression))
        {
            ThrowIfNativeOnlyForbidsFallback(mode, "Query is not natively representable");
            return null;
        }
```

Replace it with:

```csharp
        // This builder handles the whole-entity / reducer / projection / group native pipelines. It declines
        // when the query is not native at all (ClassifyNativeDisposition != Native — EF-334) AND, additionally,
        // when Route == ScalarAggregate: that shape IS native but is built by TryBuildAggregateFactory, so
        // control must fall through to it here. (A HardDecline was already thrown in VisitShapedQuery before
        // reaching this builder under Native/NativeOnly; under DriverLinq no native factory is attempted.)
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) != NativeDisposition.Native
            || mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            ThrowIfNativeOnlyForbidsFallback(mode, "Query is not natively representable");
            return null;
        }
```

Rationale for equivalence (verify while implementing): today's condition is
`Route ∈ {Fallback, ScalarAggregate} || ContainsVectorSearch`. The new condition is
`(mode != DriverLinq && IsGroupByFallbackUnsafe) || Route == Fallback || ContainsVectorSearch || Route == ScalarAggregate`.
Because `MarkGroupByFallbackUnsafe()` sets `_hasUnsupportedOperator` (so `IsGroupByFallbackUnsafe ⟹ Route == Fallback`), the extra `IsGroupByFallbackUnsafe` term is redundant, making the two conditions identical.

- [ ] **Step 4: Build and run the gate + native unit/functional tests**

Run (build first so functional tests can `--no-build`):
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation|FullyQualifiedName~NativeDisposition"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~QueryModeGate"
```
Expected: PASS for all (unit native-translation suite + `QueryModeGate*` functional tests). These exercise native/fallback/`NativeOnly`-throw behavior end-to-end.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs
git commit -m "EF-334: Route the two is-native gate sites through NativeDisposition"
```

---

### Task 3: AGENTS.md doc correction + full behavior-preservation verification

Correct the Query area docs to reflect the centralization and the streaming-axis distinction, then run the full behavior-preservation gate (byte-identical spec sweep + 3-version `/test-all`).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

- [ ] **Step 1: Update `Query/AGENTS.md`**

Find the passages that describe the gate's out-of-`Route` conditions (search for `AllPendingLookupsAreStreamable`, `ContainsVectorSearch`, and "Folding those into a single predicate is a follow-up (see EF-334)" / "Collapsing all three into one predicate"). Update them to state:
- The is-native gate decision is centralized in `ClassifyNativeDisposition` → `NativeDisposition {Native, Fallback, HardDecline}`, reading the three is-native signals in one place: `Route`, `IsGroupByFallbackUnsafe`, and `ContainsVectorSearch(CapturedExpression)`.
- `Route` remains "which native shape / slot representability"; the disposition is the superset.
- `AllPendingLookupsAreStreamable` is the **streaming-vs-DOM** axis — explicitly NOT an is-native signal — and is deliberately separate from the disposition.
- Replace the "EF-334 follow-up: collapse all three into one predicate" wording with a past-tense note that EF-334 centralized the is-native decision and that lookup-streamability stayed a separate axis.

Keep edits tight and factual; do not restructure unrelated sections.

- [ ] **Step 2: Commit the doc**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-334: Document the centralized NativeDisposition gate + streaming-axis distinction"
```

- [ ] **Step 3: Byte-identical spec-sweep verification vs baseline `5d53633`**

Capture the `NativeOnly` spec pass/fail set on the baseline, then on the EF-334 tip, and diff. (The spec suite asserts exact MQL, so an identical pass/fail set + identical run confirms MQL preservation.)

```bash
# Baseline (detached, no changes): run the native-only spec sweep and save the summary.
git stash --include-untracked 2>/dev/null; git checkout 5d53633 --quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --logger "console;verbosity=normal" -v quiet > /tmp/ef334-baseline.log 2>&1
grep -E "Passed:|Failed:|Skipped:" /tmp/ef334-baseline.log
git checkout EF-334 --quiet; git stash pop 2>/dev/null || true

# EF-334 tip:
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --logger "console;verbosity=normal" -v quiet > /tmp/ef334-ef334.log 2>&1
grep -E "Passed:|Failed:|Skipped:" /tmp/ef334-ef334.log
```
Expected: the `Passed/Failed/Skipped` counts are IDENTICAL between the two logs. If any count differs, the refactor changed behavior — stop and investigate before proceeding.

- [ ] **Step 4: Full 3-version `/test-all`**

Invoke the `/test-all` skill (build + full solution test for EF8, EF9, EF10). Expected: all three green, 0 failures, counts consistent with the `5d53633` baseline plus the 10 new `NativeDispositionTests`.

- [ ] **Step 5: (No commit — verification only.)** Report the verification results. If green, the feature is ready for branch review + squash.

---

## Self-Review

**1. Spec coverage:**
- Spec "abstraction" (3-way `NativeDisposition` + one `ClassifyNativeDisposition`) → Task 1. ✓
- Spec "call-site mapping" (hard-decline site + `TryBuildNativeFactory` site; ScalarAggregate carve-out local; projected coverage-throws unchanged) → Task 2. ✓
- Spec "correctness-critical invariant" (don't broaden signal reach) → preserved by rewriting only the two combining sites, plus the byte-identical spec sweep in Task 3 Step 3. ✓
- Spec "verification bar" (byte-identical MQL + 3-version `/test-all`) → Task 3 Steps 3–4. ✓
- Spec "docs + ticket cleanup" (AGENTS.md correction; streaming-axis distinction) → Task 3 Steps 1–2. EF-334 ticket close-note is a post-merge action, out of the code plan. ✓
- Spec "testing" (unit tests per disposition class) → Task 1 Step 1 (10 tests covering Native for each native route, Fallback route, vector-search Fallback, GroupBy-unsafe HardDecline under Native/NativeOnly, not-HardDecline under DriverLinq, precedence). ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to Task N". All code steps show complete code and exact commands. ✓

**3. Type consistency:** `NativeDisposition {Native, Fallback, HardDecline}`, `ClassifyNativeDisposition(NativeRoute, bool, bool, MongoQueryMode)` (pure) and `ClassifyNativeDisposition(MongoQueryExpression, MongoQueryMode)` (wrapper) are used identically in Tasks 1 and 2. Test helper calls the 4-arg pure overload. `Route`, `IsGroupByFallbackUnsafe`, `ContainsVectorSearch`, `ThrowIfNativeOnlyForbidsFallback` match the current source. ✓
