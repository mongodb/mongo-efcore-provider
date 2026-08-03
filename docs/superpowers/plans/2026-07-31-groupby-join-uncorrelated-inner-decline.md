# Decline a join whose inner sequence is paged (CSHARP-6017) — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Design spec:** `docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md` — read it first;
this plan does not restate the calibration or the settled decisions.

**JIRA: [EF-366](https://jira.mongodb.org/browse/EF-366)** — filed 2026-07-31, *"GroupBy over a join with an
uncorrelated Skip/Take inner silently returns wrong data via the driver-LINQ fallback"*. Use `EF-366` verbatim
in commit messages, the PR title, and every code comment this plan prescribes.

**Goal:** A `Join`/`GroupJoin`/`LeftJoin` whose **inner sequence carries `Skip`/`Take`** hard-declines with
`NativeTranslationNotSupportedException` under `Native`/`NativeOnly` instead of routing to a driver-LINQ
fallback that driver 3.10 mistranslates (CSHARP-6017) into silently wrong rows. A wrong-data verdict already
reached on a **nested** inner subquery propagates to the outer query so the gate sees it. Nothing that is
correct today changes behaviour.

**Architecture:** Two production changes, both narrow.
(1) `MongoSelectDefinition` gains a sibling wrong-data flag `IsPagedJoinInnerFallbackUnsafe` next to the
existing `IsGroupByFallbackUnsafe`, a union `IsFallbackWrongData` that the gate reads instead of the
GroupBy-specific flag, a `PropagateFallbackWrongDataFrom` copier, and a `HasPagingAnywhere` predicate.
(2) `MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore` sets the new flag when
`innerQueryExpression.Select.HasPagingAnywhere`, and propagates any wrong-data verdict from the inner select to
the outer. The whole decline path (`ClassifyNativeDisposition` → `NativeDisposition.HardDecline` →
`VisitShapedQuery` throws at compile time; explicit `DriverLinq` still executes) is reused unchanged in
structure.

**Tech Stack:** C# / EF Core provider. xUnit with **plain `Assert.*`** (the Query native test suites do not use
FluentAssertions — follow the local convention in `NativeGroupByTests.cs`). Multi-EF via the
`Debug EF8|EF9|EF10` build *configurations*.

## Global Constraints

- **No `#if` in `src/`.** The hazard and the fix are identical on EF8/EF9/EF10 (measured — design spec §2.4).
  `#if` is permitted only in tests where a *baseline* already differs by version.
- **`<Nullable>enable</Nullable>`** on `src/` — annotate new members accordingly. All new members are
  `internal`, so this is not a public-API change.
- **Preserve each file's existing BOM state.** Use targeted edits, never a full rewrite. Note the repo is
  *mixed* (~47 of 60 sampled `src/*.cs` have a BOM) and the blanket claim that every file here has one is
  **false**: `MongoSelectDefinition.cs` and `MongoSelectDefinitionTests.cs` have **no** BOM (verified at
  `a0774bf`). Do not add one to a file that lacks it — check with
  `head -c3 <file> | xxd -p` before assuming either way.
- **Tests run serially.** Leave `MONGODB_URI` and `ATLAS_URI` **unset** so TestContainers boots an isolated
  `mongodb/mongodb-atlas-local` per test process.
- **Build:** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (likewise `EF8` / `EF9`).
- **Every `TODO(CSHARP-6017)` marker is part of the deliverable.** `grep -rn "CSHARP-6017" src tests docs` must
  be a complete removal checklist when the driver is fixed (design spec §2.6).
- **`PropagateFallbackWrongDataFrom` is NOT part of the CSHARP-6017 guard** — it fixes an independent EF-344
  nesting hole and must survive the guard's eventual removal. Comment it as such.
- Namespaces: `MongoSelectDefinition`, `MongoSkipOp`, `MongoLimitOp`, `NativeRoute` in
  `MongoDB.EntityFrameworkCore.Query.Expressions`; the gate and `NativeDisposition` in
  `MongoDB.EntityFrameworkCore.Query.Visitors`; `MongoQueryMode` in `MongoDB.EntityFrameworkCore.Infrastructure`;
  `NativeTranslationNotSupportedException` in `MongoDB.EntityFrameworkCore.Query.NativeTranslation`.
- Commit after each task. One commit per task; the **first** commit message carries the JIRA number.

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` | modify (~L131–141 paging predicates; ~L338–360 flag block) | Adds `HasPagingAnywhere`, `_isPagedJoinInnerFallbackUnsafe` / `IsPagedJoinInnerFallbackUnsafe` / `MarkPagedJoinInnerFallbackUnsafe`, `IsFallbackWrongData`, `PropagateFallbackWrongDataFrom` |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` | modify (L163–176 `VisitShapedQuery` decline; L748–792 `ClassifyNativeDisposition`) | Reads `IsFallbackWrongData` instead of `IsGroupByFallbackUnsafe`; renames the pure overload's parameter; emits a cause-specific decline message |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | modify (`TranslateJoinCore`, L1268–1296) | The paging predicate and the inner→outer wrong-data propagation |
| `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | modify (L39) | Corrects the falsified "join-then-group falls back correctly" claim; documents the new decline |
| `BREAKING-CHANGES.md` | modify (unreleased "8.5.0 / 9.2.0 / 10.1.0" section) | The behaviour-change entry settled in design spec §2.7 |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` | modify (after `Take_before_skip_records_both_ops_in_arrival_order`, ~L120) | Unit-pins `HasPagingAnywhere`, the flags, `IsFallbackWrongData`, `PropagateFallbackWrongDataFrom` |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` | modify (L25–31 helper; L64–87 named args) | Renames the helper parameter; keeps the four hard-decline cases pinned |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinPagedInnerDeclineTests.cs` | **create** | All end-to-end pins from design spec §2.8, including the wrong-data `[Fact]` and the CSHARP-6017 expiry tripwire |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/CrossCollectionIncludeTests.cs` | modify (append one `[Fact]` inside the existing `#if !EF8 && !EF9` region) | Control: filtered `Include` with paging must stay native and correct (guards against over-declining, candidate P5) |
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs` | modify (L961–970, L1026–1035, L1318–1327, L1897–1910, L1923–1936, L2324–2337) | 3 wrong-data methods pass as written with corrected comments/baselines; 3 drift methods retargeted / re-baselined |
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindJoinQueryMongoTest.cs` | modify (L159–162, L169–172, L184–187, L275–278, L489–492, L701–704 un-skip; L494–506 + L508–530 re-baseline) | Removes the 6 `CSHARP-6017` skips, retargets them; re-baselines 2 tautology tests |
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs` | modify (L1428–1445) | Re-baselines `Reverse_in_join_inner_with_skip` |

---

### Task 1: `MongoSelectDefinition` — paging predicate and wrong-data flags

Pure state on the query IR, unit-tested in isolation. Nothing sets or reads the new members yet, so this task
cannot change behaviour.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (paging predicates ~L131–141; flag block ~L338–360)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (append after `Take_before_skip_records_both_ops_in_arrival_order`, ~L120)

**Interfaces:**
- Consumes: `MongoSkipOp`, `MongoLimitOp`, `MongoSelectDefinition.HasPaging`, `_trailingOps`, `_hasUnsupportedOperator`, `MarkGroupByFallbackUnsafe()`
- Produces:
  - `internal bool MongoSelectDefinition.HasPagingAnywhere { get; }`
  - `internal bool MongoSelectDefinition.IsPagedJoinInnerFallbackUnsafe { get; }`
  - `internal void MongoSelectDefinition.MarkPagedJoinInnerFallbackUnsafe()`
  - `internal bool MongoSelectDefinition.IsFallbackWrongData { get; }`
  - `internal void MongoSelectDefinition.PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)`

**Steps:**

- [ ] Add the failing unit tests. Append to `MongoSelectDefinitionTests.cs`:

```csharp
    [Fact]
    public void HasPagingAnywhere_is_false_with_no_paging()
        => Assert.False(new MongoSelectDefinition().HasPagingAnywhere);

    [Fact]
    public void HasPagingAnywhere_sees_pipeline_ops()
    {
        var s = new MongoSelectDefinition();
        s.AppendSkip(Const(5));

        Assert.True(s.HasPagingAnywhere);
    }

    [Fact]
    public void HasPagingAnywhere_sees_trailing_ops_after_a_set_op()
    {
        // A Take composed AFTER a set operation records into _trailingOps, which HasPaging deliberately does
        // not scan (its consumer gates a PRE-terminal GroupBy). The CSHARP-6017 join guard must still see it.
        var s = new MongoSelectDefinition();
        s.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "OtherCollection");
        s.AppendLimit(Const(3));

        Assert.Empty(s.PipelineOps);
        Assert.False(s.HasPaging);
        Assert.True(s.HasPagingAnywhere);
    }

    [Fact]
    public void Fallback_wrong_data_is_false_by_default()
    {
        var s = new MongoSelectDefinition();

        Assert.False(s.IsFallbackWrongData);
        Assert.False(s.IsGroupByFallbackUnsafe);
        Assert.False(s.IsPagedJoinInnerFallbackUnsafe);
    }

    [Fact]
    public void MarkPagedJoinInnerFallbackUnsafe_sets_the_flag_and_forces_fallback_route()
    {
        var s = new MongoSelectDefinition();
        s.MarkPagedJoinInnerFallbackUnsafe();

        Assert.True(s.IsPagedJoinInnerFallbackUnsafe);
        Assert.True(s.IsFallbackWrongData);
        Assert.False(s.IsGroupByFallbackUnsafe);
        Assert.Equal(NativeRoute.Fallback, s.Route);
    }

    [Fact]
    public void PropagateFallbackWrongDataFrom_copies_both_provenances_independently()
    {
        var groupByInner = new MongoSelectDefinition();
        groupByInner.MarkGroupByFallbackUnsafe();
        var outer1 = new MongoSelectDefinition();
        outer1.PropagateFallbackWrongDataFrom(groupByInner);

        Assert.True(outer1.IsGroupByFallbackUnsafe);
        Assert.False(outer1.IsPagedJoinInnerFallbackUnsafe);

        var pagedInner = new MongoSelectDefinition();
        pagedInner.MarkPagedJoinInnerFallbackUnsafe();
        var outer2 = new MongoSelectDefinition();
        outer2.PropagateFallbackWrongDataFrom(pagedInner);

        Assert.True(outer2.IsPagedJoinInnerFallbackUnsafe);
        Assert.False(outer2.IsGroupByFallbackUnsafe);
    }

    [Fact]
    public void PropagateFallbackWrongDataFrom_a_clean_inner_is_a_no_op()
    {
        var outer = new MongoSelectDefinition();
        outer.PropagateFallbackWrongDataFrom(new MongoSelectDefinition());

        Assert.False(outer.IsFallbackWrongData);
        Assert.Equal(NativeRoute.WholeEntity, outer.Route);
    }
```

- [ ] Run them and see them fail to **compile** (the members do not exist):
      `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`.
      If `Const(...)`, `MongoSetOperation` or `MongoSetOperationKind` are not already in scope in that test
      file, adjust the arrangement to whatever the file's existing helpers provide — the assertions are the
      point, not the arrangement.
- [ ] Add `HasPagingAnywhere` to `MongoSelectDefinition.cs`, immediately after `HasLimit` (~L141):

```csharp
    /// <summary>
    /// <see langword="true"/> when any <c>$skip</c> or <c>$limit</c> op is present in EITHER op list. Unlike
    /// <see cref="HasPaging"/> — which deliberately scans <c>_pipelineOps</c> only, because its consumer gates a
    /// PRE-terminal GroupBy that is unreachable after a set op — this must see paging wherever it was recorded,
    /// including a <c>Take</c> composed AFTER a set operation (which lands in <see cref="TrailingOps"/>).
    /// Read by the QMTEV's <c>TranslateJoinCore</c> CSHARP-6017 guard: "does this sequence page itself?".
    /// TODO(CSHARP-6017): delete together with <see cref="MarkPagedJoinInnerFallbackUnsafe"/> when the driver
    /// stops folding an uncorrelated join inner's paging into the correlated <c>$lookup</c> sub-pipeline.
    /// </summary>
    internal bool HasPagingAnywhere
        => HasPaging || _trailingOps.Exists(o => o is MongoSkipOp or MongoLimitOp);
```

- [ ] Add the flag block to `MongoSelectDefinition.cs`, immediately after `MarkGroupByFallbackUnsafe()` (~L360):

```csharp
    private bool _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// <see langword="true"/> when this query contains a <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c> whose
    /// INNER sequence pages itself (<c>Skip</c>/<c>Take</c>). Driver 3.10 mistranslates that shape —
    /// <b>CSHARP-6017</b> — by folding the uncorrelated inner's <c>$sort</c>/<c>$skip</c>/<c>$limit</c> into the
    /// CORRELATED <c>$lookup</c> sub-pipeline, where they run per-outer-row over a key-matched subset of at most
    /// one document instead of once over the whole inner sequence. The driver-LINQ fallback therefore executes
    /// and returns <em>silently wrong</em> rows (measured: 0 rows where 453 is correct; 830 where 181 is
    /// correct). Like <see cref="IsGroupByFallbackUnsafe"/> this must HARD-decline under
    /// <c>Native</c>/<c>NativeOnly</c> rather than fall back; explicit <c>DriverLinq</c> stays the user's opt-in.
    /// A separate flag from <see cref="IsGroupByFallbackUnsafe"/> so the decline message can name the real cause
    /// and so this one can be deleted wholesale when the driver is fixed.
    /// TODO(CSHARP-6017): delete this flag, its setter and <see cref="HasPagingAnywhere"/> on driver fix — see
    /// the removal checklist in docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6.
    /// </summary>
    internal bool IsPagedJoinInnerFallbackUnsafe => _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// Records that this query joins against an inner sequence that pages itself, whose driver-LINQ fallback
    /// returns wrong rows (see <see cref="IsPagedJoinInnerFallbackUnsafe"/>). Also marks the query non-native.
    /// </summary>
    internal void MarkPagedJoinInnerFallbackUnsafe()
    {
        _isPagedJoinInnerFallbackUnsafe = true;
        _hasUnsupportedOperator = true;
    }

    /// <summary>
    /// <see langword="true"/> when ANY wrong-data-on-fallback provenance has been recorded — a GroupBy combined
    /// with a join (<see cref="IsGroupByFallbackUnsafe"/>) or a self-paging join inner
    /// (<see cref="IsPagedJoinInnerFallbackUnsafe"/>). This is the single signal the gate reads: both mean "the
    /// driver-LINQ fallback executes and returns wrong rows", so both hard-decline identically. See
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition</c>.
    /// </summary>
    internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe || _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// Copies any wrong-data provenance from <paramref name="inner"/> onto this select. A join whose inner is
    /// itself a SUBQUERY containing an offending shape records the verdict on the INTERMEDIATE
    /// <c>MongoQueryExpression</c>, and the gate only ever reads the OUTERMOST one — measured: the spec's
    /// <c>Join_GroupBy_Aggregate_in_subquery</c> inner declines correctly when promoted to top level but
    /// executes and returns 0 rows (expected 133) when nested. Propagation is what makes the verdict
    /// nesting-insensitive.
    /// NOT part of the CSHARP-6017 guard: this closes an independent EF-344 hole and must SURVIVE the driver
    /// fix. Do not delete it with the paged-inner flag.
    /// </summary>
    internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
    {
        if (inner._isGroupByFallbackUnsafe)
        {
            MarkGroupByFallbackUnsafe();
        }

        if (inner._isPagedJoinInnerFallbackUnsafe)
        {
            MarkPagedJoinInnerFallbackUnsafe();
        }
    }
```

- [ ] Re-run the unit tests and see all seven pass.
- [ ] `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` — clean.
- [ ] Commit: `EF-366: Decline a join whose inner sequence is paged (CSHARP-6017)` (this is the first commit,
      so it carries the ticket number and the slice title).

---

### Task 2: Gate reads the union signal

`ClassifyNativeDisposition` stops being GroupBy-specific. Still no behaviour change: nothing sets the new flag
until Task 4.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (L163–176, L748–792)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` (L25–31, L64–87)

**Interfaces:**
- Consumes: `MongoSelectDefinition.IsFallbackWrongData`, `MongoSelectDefinition.IsPagedJoinInnerFallbackUnsafe`, `MongoQueryMode`, `NativeRoute`
- Produces: `internal static NativeDisposition ClassifyNativeDisposition(NativeRoute route, bool isFallbackWrongData, bool containsVectorSearch, MongoQueryMode mode)` — same arity, renamed second parameter

**Steps:**

- [ ] Rename the parameter in the test helper and its call sites in `NativeDispositionTests.cs`
      (`isGroupByFallbackUnsafe` → `isFallbackWrongData`, 1 declaration + 1 forward + 4 named arguments), and
      rename the four hard-decline test methods to drop the `GroupBy_` prefix
      (`Fallback_wrong_data_is_hard_decline_under_native`, `…_under_native_only`,
      `…_is_fallback_under_driver_linq`, `Hard_decline_takes_precedence_over_vector_search` keeps its name):

```csharp
    private static NativeDisposition Classify(
        NativeRoute route,
        bool isFallbackWrongData = false,
        bool containsVectorSearch = false,
        MongoQueryMode mode = MongoQueryMode.Native)
        => MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(
            route, isFallbackWrongData, containsVectorSearch, mode);
```

- [ ] Run and see it fail to compile (the production parameter is still named `isGroupByFallbackUnsafe`).
- [ ] In `MongoShapedQueryCompilingExpressionVisitor.cs`, rename the pure overload's parameter and broaden its
      doc + the in-body comment (L748–776):

```csharp
    /// <param name="isFallbackWrongData">Whether this query's driver-LINQ fallback returns silently wrong rows —
    /// a GroupBy combined with a join, or a join whose inner sequence pages itself (CSHARP-6017). See
    /// <see cref="MongoSelectDefinition.IsFallbackWrongData"/>.</param>
    internal static NativeDisposition ClassifyNativeDisposition(
        NativeRoute route,
        bool isFallbackWrongData,
        bool containsVectorSearch,
        MongoQueryMode mode)
    {
        // A query whose driver-LINQ fallback returns wrong rows must hard-decline under Native/NativeOnly.
        // Explicit DriverLinq is the user's opt-in and runs it (not a hard decline). Checked first so it takes
        // precedence over the graceful-fallback signals.
        if (mode != MongoQueryMode.DriverLinq && isFallbackWrongData)
        {
            return NativeDisposition.HardDecline;
        }
```

- [ ] Point the gathering overload at the union signal (L785–792): change
      `q.Select.IsGroupByFallbackUnsafe,` to `q.Select.IsFallbackWrongData,`.
- [ ] Make the decline message name the actual cause in `VisitShapedQuery` (L169–176):

```csharp
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) == NativeDisposition.HardDecline)
        {
            // TODO(CSHARP-6017): drop the paged-inner arm when the driver stops folding an uncorrelated join
            // inner's $sort/$skip/$limit into the correlated $lookup sub-pipeline.
            var cause = mongoQueryExpression.Select.IsPagedJoinInnerFallbackUnsafe
                ? "Query joins against an inner sequence that applies Skip/Take to itself, which the native "
                  + "translator does not support and which the MongoDB driver's LINQ provider mistranslates "
                  + "(CSHARP-6017), returning incorrect results"
                : "Query combines GroupBy with a Join, which the native translator does not support and whose "
                  + "driver-LINQ fallback returns incorrect results";
            throw new NativeTranslationNotSupportedException(
                cause + "; use MongoQueryMode.DriverLinq to opt in to the driver-LINQ execution of this query.");
        }
```

- [ ] Update the `NativeDisposition.HardDecline` XML summary (L62) so it no longer says "(GroupBy+Join)":
      `/// <summary>Must throw under <see cref="MongoQueryMode.Native"/> AND <see cref="MongoQueryMode.NativeOnly"/>: the driver-LINQ fallback returns wrong rows (GroupBy+Join, or a self-paging join inner).</summary>`
- [ ] Re-run `NativeDispositionTests` — all pass.
- [ ] Run the existing GroupBy+Join end-to-end pins to prove no behaviour change:
      `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByTests"`
      — in particular `GroupBy_combined_with_Join_throws_clean_translation_failure_under_native`,
      `GroupBy_combined_with_Join_still_runs_under_driver_linq`,
      `GroupBy_over_a_joined_source_runs_correctly_under_native`.
- [ ] Commit: `EF-366: gate reads a union wrong-data-on-fallback signal`.

---

### Task 3: The regression net — controls that must be green BEFORE the guard exists

These tests encode "what must not change". They are written and passing *before* any guard, so if Task 4
over-declines they turn red immediately rather than at sweep time. The CSHARP-6017 expiry tripwire also lands
here, because it too must pass before the guard (it exercises `DriverLinq`, which the guard never blocks).

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinPagedInnerDeclineTests.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/CrossCollectionIncludeTests.cs` (append one `[Fact]` inside the existing `#if !EF8 && !EF9` region, before the private helpers at ~L322)

**Interfaces:**
- Consumes: `TemporaryDatabaseFixture`, `TemporaryDatabaseFixtureBase.CreateCollectionName`, `MongoQueryMode`, `NativeTranslationNotSupportedException`, `MongoDBExtensions.UseMongoDB`, `ToCollection`
- Produces: `NativeJoinPagedInnerDeclineTests` with the fixture `PagedJoinDbContext` (`DbSet<Order> Orders`, `DbSet<Region> Regions`) and `CreateContext(MongoQueryMode mode, string name)`

**Steps:**

- [ ] Create the new file with the fixture and the three control tests plus the tripwire. Seed arithmetic:
      Orders = US/100, US/200, UK/50, UK/25, FR/300; Regions = US/NA, UK/EU, FR/EU.
      `Regions.OrderBy(Country).Take(2)` = FR, UK, so the **correct** join answer is the 3 rows
      `FR:EU, UK:EU, UK:EU`; the CSHARP-6017 fold keeps every order's single key match and returns **5**.

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

using System;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A Join/GroupJoin/LeftJoin whose INNER sequence pages itself (Skip/Take) is declined by the provider, because
/// the MongoDB driver's LINQ provider mistranslates it — CSHARP-6017 — by folding the uncorrelated inner's
/// $sort/$skip/$limit into the CORRELATED $lookup sub-pipeline, where they run per-outer-row over a key-matched
/// subset of at most one document. The fallback therefore returns silently WRONG rows, so the shape must
/// hard-decline rather than fall back.
/// TODO(CSHARP-6017): delete this whole file when the driver is fixed — see the removal checklist in
/// docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6. The tripwire test
/// at the bottom is what announces the fix.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeJoinPagedInnerDeclineTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // The correct answer for PagedInnerJoin below: Regions ordered by Country are FR, UK, US; Take(2) keeps
    // FR and UK; the orders in those two countries are FR/300, UK/50, UK/25.
    private static readonly string[] CorrectRows = ["FR:EU", "UK:EU", "UK:EU"];

    // What the CSHARP-6017 fold returns instead: the $sort/$limit run inside the per-order $lookup, where every
    // order's single key match survives, so all five orders join.
    private const int FoldedWrongRowCount = 5;

    private static string[] PagedInnerJoin(PagedJoinDbContext db)
        => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Take(2),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

    [Fact]
    public void Join_with_paged_outer_still_runs_and_is_correct()
    {
        // CONTROL for an over-broad predicate that looks at the OUTER side too. Paging on the outer is emitted
        // at pipeline TOP LEVEL, before the $lookup, and is correct — it must keep working.
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_paged_outer_still_runs_and_is_correct));

        var rows = db.Orders.OrderBy(o => o.Amount).Take(2)
            .Join(db.Regions, o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

        // The two cheapest orders are UK/25 and UK/50.
        Assert.Equal(["UK:EU", "UK:EU"], rows);
    }

    [Fact]
    public void Join_with_reshaped_unpaged_inner_still_runs_and_is_correct()
    {
        // CONTROL for an over-broad predicate keyed on "the inner is a reshaping subquery". Driver 3.10 folds an
        // unpaged inner's $sort (or nothing at all) into the $lookup sub-pipeline, which is BENIGN: order within
        // a single-document key match is a no-op. Measured correct; must not be declined.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_reshaped_unpaged_inner_still_runs_and_is_correct));

        var rows = db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Select(r => new { r.Country, r.Continent }),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

        Assert.Equal(["FR:EU", "UK:EU", "UK:EU", "US:NA", "US:NA"], rows);
    }

    [Fact]
    public void Join_with_paged_inner_still_runs_under_driver_linq()
    {
        // Explicit DriverLinq is the user's documented opt-in to the previous path, wrong-data caveat included —
        // exactly as for the GroupBy+Join decline. It must never throw NativeTranslationNotSupportedException.
        using var db = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Join_with_paged_inner_still_runs_under_driver_linq));

        var ex = Record.Exception(() => PagedInnerJoin(db));

        Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
    }

    [Fact]
    public void Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017()
    {
        // EXPIRY TRIPWIRE, not a desired behavior. It pins the CSHARP-6017 driver defect that the provider guard
        // exists for, using the only mode that still reaches the driver. The CORRECT answer is CorrectRows (3
        // rows); the driver returns 5 because it folds $sort/$limit into the correlated $lookup sub-pipeline.
        //
        // WHEN THIS TEST FAILS, THE DRIVER HAS BEEN FIXED. Follow the removal checklist in
        // docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6:
        // delete this file, the HasPagingAnywhere block in TranslateJoinCore, MongoSelectDefinition's
        // MarkPagedJoinInnerFallbackUnsafe/IsPagedJoinInnerFallbackUnsafe/HasPagingAnywhere, collapse
        // IsFallbackWrongData back to IsGroupByFallbackUnsafe, and revert the spec-suite retargets. Do NOT
        // delete PropagateFallbackWrongDataFrom — it fixes an unrelated EF-344 nesting hole.
        using var db = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017));

        var rows = PagedInnerJoin(db);

        Assert.Equal(FoldedWrongRowCount, rows.Length);
        Assert.NotEqual(CorrectRows, rows);
    }

    private PagedJoinDbContext CreateContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;

        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2020, Amount = 100 },
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2021, Amount = 200 },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 50 },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 25 },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Year = 2021, Amount = 300 },
        ]);
        database.MongoDatabase.GetCollection<Region>(regionsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Continent = "NA" },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Continent = "EU" },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Continent = "EU" },
        ]);

        return new PagedJoinDbContext(database, ordersName, regionsName, mode);
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
    }

    private class Region
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
    }

    private class PagedJoinDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _regionsCollection;

        public PagedJoinDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<PagedJoinDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _regionsCollection = regionsCollection;
        }

        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Region> Regions { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().ToCollection(_ordersCollection);
            modelBuilder.Entity<Region>().ToCollection(_regionsCollection);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
```

- [ ] Run the four tests and see **all four pass** (no guard exists yet, so the two controls and the DriverLinq
      test pass trivially, and the tripwire confirms the driver defect is present at the pinned driver version):
      `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeJoinPagedInnerDeclineTests"`.
      If the tripwire does **not** see 5 rows, stop: the driver may already have been fixed, in which case this
      whole slice is unnecessary and the finding must go back to the owner.
- [ ] Add the filtered-`Include` control to `CrossCollectionIncludeTests.cs`, inside the existing
      `#if !EF8 && !EF9` region (cross-collection `Include` *query* translation is EF10-only in this suite):

```csharp
    [Fact]
    public void Filtered_include_with_paging_still_runs_and_is_correct()
    {
        // CONTROL for an over-broad CSHARP-6017 guard (see NativeJoinPagedInnerDeclineTests). A FILTERED Include
        // puts $sort/$skip/$limit inside a native "_lookup_<Nav>" sub-pipeline too, but there the per-outer-row
        // semantics are exactly what Include means, so the result is CORRECT and must not be declined. The
        // paging here lives on a NAVIGATION, not on a Queryable.Join inner, so the guard's site
        // (TranslateJoinCore) never sees it — this test is what keeps that true.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();
        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var alice = db.Customers
            .Where(c => c.FullName == "Alice")
            .Include(c => c.Orders.OrderBy(o => o.OrderDescription).Take(1))
            .Single();

        Assert.Equal(["Order 1"], alice.Orders.Select(o => o.OrderDescription).ToArray());
    }
```

- [ ] Run it and see it pass:
      `dotnet test … -c "Debug EF10" --filter "FullyQualifiedName~CrossCollectionIncludeTests.Filtered_include_with_paging_still_runs_and_is_correct"`.
- [ ] Commit: `EF-366: regression net + CSHARP-6017 expiry tripwire`.

---

### Task 4: Decline a join whose inner sequence is paged

The guard itself. TDD: three failing tests first.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinPagedInnerDeclineTests.cs` (add 4 tests)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateJoinCore`, after the `IsGroupBy`/`IsDistinct` block ending ~L1296)

**Interfaces:**
- Consumes: `MongoSelectDefinition.HasPagingAnywhere`, `MongoSelectDefinition.MarkPagedJoinInnerFallbackUnsafe()`
- Produces: no new API — behaviour only

**Steps:**

- [ ] Add the failing tests to `NativeJoinPagedInnerDeclineTests.cs`:

```csharp
    [Fact]
    public void Join_with_paged_inner_declines_under_native()
    {
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_paged_inner_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() => PagedInnerJoin(db));
    }

    [Fact]
    public void Join_with_paged_inner_declines_under_native_only()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Join_with_paged_inner_declines_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() => PagedInnerJoin(db));
    }

    [Fact]
    public void Join_with_paged_inner_never_returns_the_wrong_rows_under_native()
    {
        // MUTATION PIN for the data, deliberately NOT phrased as "it throws" — that is the job of
        // Join_with_paged_inner_declines_under_native, and a wrong-rows assertion placed AFTER a decline
        // assertion in the same method is unreachable exactly when the guard is deleted. Here the data
        // comparison is the branch that RUNS under mutation: delete the guard and the query executes, returns
        // the folded 5 rows, and Assert.Equal fails. Only two outcomes are acceptable — a clean decline, or the
        // correct rows (which is also what makes this test survive the eventual driver fix).
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_paged_inner_never_returns_the_wrong_rows_under_native));

        string[]? rows = null;
        var ex = Record.Exception(() => rows = PagedInnerJoin(db));

        if (ex is null)
        {
            Assert.Equal(CorrectRows, rows);
        }
        else
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
    }

    [Fact]
    public void GroupJoin_with_paged_inner_declines_under_native()
    {
        // The GroupJoin / flattened-left-join spelling routes through the same TranslateJoinCore on every EF
        // version (on EF8/EF9 a LeftJoin is written this way), so it must decline identically.
        using var db = CreateContext(MongoQueryMode.Native, nameof(GroupJoin_with_paged_inner_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            (from o in db.Orders
             join r in db.Regions.OrderBy(x => x.Country).Take(2) on o.Country equals r.Country into rs
             from r in rs
             select new { o.Country, r.Continent }).ToArray());
    }
```

- [ ] Run them and see all four fail — the three `Assert.Throws` because the query executes, and
      `…never_returns_the_wrong_rows…` because it returns the 5 folded rows.
- [ ] Add the guard in `TranslateJoinCore`, as an **independent `if`** *after* the existing
      `IsGroupBy` / `else if (IsDistinct)` chain and *before* `outerQueryExpression.AddInnerCollection(...)`:

```csharp
        // CSHARP-6017 (driver 3.10). The driver's LINQ provider folds an UNCORRELATED join inner's
        // $sort/$skip/$limit into the CORRELATED $lookup sub-pipeline, where they run per-outer-row over a
        // key-matched subset of at most one document instead of once over the whole inner sequence. Measured:
        // Orders.Join(Customers.OrderBy(City).Skip(10).Take(50), …) returns 0 rows where 453 is correct;
        // …Select(new{…}).Take(20) returns 830 where 181 is correct — silently wrong data, with or without a
        // GroupBy anywhere in the query. So decline rather than route to that fallback.
        // The predicate is exactly "the inner sequence pages ITSELF": the fold is benign for a $sort alone
        // (order within a single-document key match is a no-op) and for no fold at all, both measured correct —
        // and Skip/Take is also precisely what separates the six CSHARP-6017-skipped NorthwindJoinQueryMongoTest
        // cases from their currently-green non-Take siblings. Nothing about the OUTER side is examined: the
        // outer's own paging is emitted at pipeline top level and is correct.
        // No correlation test is needed. A Queryable.Join/GroupJoin/LeftJoin inner is uncorrelated BY
        // CONSTRUCTION (it is an argument, not a lambda over the outer element); a correlated paged inner can
        // only be written as SelectMany, which TranslateSelectMany declines (=> null) so EF fails translation
        // outright — measured. A filtered Include's paging lives on a NAVIGATION and never reaches here, which
        // is why it keeps working (its per-outer-row sub-pipeline is exactly what Include means).
        // TODO(CSHARP-6017): delete this block, MongoSelectDefinition.MarkPagedJoinInnerFallbackUnsafe /
        // IsPagedJoinInnerFallbackUnsafe / HasPagingAnywhere, and NativeJoinPagedInnerDeclineTests when the
        // driver stops folding. The tripwire test in that file announces the fix. Do NOT delete the
        // PropagateFallbackWrongDataFrom call below — it closes an independent EF-344 nesting hole.
        if (innerQueryExpression.Select.HasPagingAnywhere)
        {
            outerQueryExpression.Select.MarkPagedJoinInnerFallbackUnsafe();
        }

        // A wrong-data verdict reached on the INNER select must reach the gate, which only ever reads the
        // OUTERMOST MongoQueryExpression. When the offending shape lives in a SUBQUERY used as this join's
        // inner, MarkGroupByFallbackUnsafe/MarkPagedJoinInnerFallbackUnsafe wrote to that intermediate select
        // and the verdict would otherwise be lost — measured: the spec's Join_GroupBy_Aggregate_in_subquery
        // inner declines correctly when promoted to top level but executes and returns 0 rows (expected 133)
        // when nested. Independent of CSHARP-6017; keep on driver fix.
        outerQueryExpression.Select.PropagateFallbackWrongDataFrom(innerQueryExpression.Select);
```

- [ ] Re-run the whole new test class and see all eight pass (four new + the four controls/tripwire from
      Task 3, which must still be green).
- [ ] Re-run `CrossCollectionIncludeTests` in full — the filtered-`Include` control and every existing test must
      still pass:
      `dotnet test … -c "Debug EF10" --filter "FullyQualifiedName~CrossCollectionIncludeTests"`.
- [ ] Re-run `NativeGroupByTests` and `NativeSelectManyTests` in full — no change expected.
- [ ] Commit: `EF-366: decline a Join/GroupJoin/LeftJoin whose inner sequence pages itself`.

---

### Task 5: Propagate a nested wrong-data verdict

The propagation statement is already in place from Task 4 (the two changes sit together in the source and would
be awkward to interleave), so this task's job is to **pin it independently** — a reviewer must be able to reject
the propagation while accepting the paging guard, which requires its own failing-then-passing test.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinPagedInnerDeclineTests.cs` (add 1 test)
- Modify: none (verification task for `PropagateFallbackWrongDataFrom`)

**Interfaces:**
- Consumes: `MongoSelectDefinition.PropagateFallbackWrongDataFrom`
- Produces: no new API

**Steps:**

- [ ] Add the test. Its inner subquery is grouped-and-joined but carries **no** paging, so only propagation can
      make it decline:

```csharp
    [Fact]
    public void Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native()
    {
        // Mirrors the spec's Join_GroupBy_Aggregate_in_subquery. The wrong-data shape (a join over a GROUPED
        // source) is in a SUBQUERY used as the outer join's inner, so MarkGroupByFallbackUnsafe lands on the
        // intermediate MongoQueryExpression, not on the one the gate reads. There is NO paging anywhere here, so
        // the CSHARP-6017 guard cannot fire: only PropagateFallbackWrongDataFrom makes this decline. Deleting
        // that call makes this test fail (the query executes and returns wrong rows) while every other test in
        // this file still passes.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            (from o in db.Orders
             join i in (from r in db.Regions
                        join a in db.Orders.GroupBy(x => x.Country)
                                .Select(g => new { Country = g.Key, Max = g.Max(x => x.Amount) })
                            on r.Country equals a.Country
                        select new { r, a.Max })
                 on o.Country equals i.r.Country
             select new { o.Year, i.Max }).ToArray());
    }
```

- [ ] Run it and see it pass. Then **verify it is a real pin**: temporarily comment out the
      `PropagateFallbackWrongDataFrom` call, re-run, and confirm this test fails while
      `Join_with_paged_inner_declines_under_native` still passes. Restore the call.
- [ ] Also confirm the converse pin: temporarily comment out the `HasPagingAnywhere` block, re-run, and confirm
      the four paging tests fail while this one still passes. Restore.
- [ ] Commit: `EF-366: propagate a nested wrong-data verdict to the outer query`.

---

### Task 6: Spec suite — the three wrong-data GroupBy methods

These three now decline, so they pass as written. Their stale `// Fails: GroupBy issue EF-149` comments and MQL
baselines must be corrected — the baselines were the *fingerprint of a driver-LINQ translation failure* (a
collection name with an empty pipeline) and there is now no MQL at all, because the decline happens at compile
time before any pipeline is built.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs` (L961–970, L1026–1035, L1318–1327)

**Interfaces:**
- Consumes: the file's `protected new static Task AssertTranslationFailed(Func<Task> query)` shadow (L2549)
- Produces: no API

**Steps:**

- [ ] Run the three methods first and record what MQL, if any, is emitted:
      `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.Join_complex_GroupBy_Aggregate|FullyQualifiedName~NorthwindGroupByQueryMongoTest.GroupJoin_complex_GroupBy_Aggregate|FullyQualifiedName~NorthwindGroupByQueryMongoTest.Join_GroupBy_Aggregate_in_subquery"`.
      The expectation is that each fails only on `AssertMql` (the recorded `Orders.` / `Customers.` line is no
      longer emitted); use the actual reported MQL to write the baseline, never a guess.
- [ ] Rewrite the three bodies. `Join_complex_GroupBy_Aggregate` (L961):

```csharp
    public override async Task Join_complex_GroupBy_Aggregate(bool async)
    {
        // Declines: the join's inner is `Customers.Where(…).OrderBy(City).Skip(10).Take(50)` — a self-paging
        // inner, which driver 3.10 mistranslates (CSHARP-6017) into a $lookup sub-pipeline that returns 0 rows
        // instead of 29. The provider hard-declines the shape rather than route to that fallback, so no MQL is
        // emitted. TODO(CSHARP-6017): on driver fix this goes back to `await base.…` with a real MQL baseline.
        await AssertTranslationFailed(() => base.Join_complex_GroupBy_Aggregate(async));

        AssertMql();
    }
```

      `GroupJoin_complex_GroupBy_Aggregate` (L1026) — same wording, inner
      `Orders.Where(< 10400).OrderBy(OrderDate).Take(100)`, "27 rows instead of 20".
      `Join_GroupBy_Aggregate_in_subquery` (L1318):

```csharp
    public override async Task Join_GroupBy_Aggregate_in_subquery(bool async)
    {
        // Declines: the join's inner is a SUBQUERY that itself joins a grouped source, so the wrong-data verdict
        // is reached on the intermediate query expression and propagated to the outer one
        // (MongoSelectDefinition.PropagateFallbackWrongDataFrom). Without that propagation the driver-LINQ
        // fallback executed and returned 0 rows instead of 133. No paging is involved, so this is NOT the
        // CSHARP-6017 guard and it stays after the driver is fixed.
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_in_subquery(async));

        AssertMql();
    }
```

- [ ] Re-run the three on EF10 and see them pass. If a baseline still shows MQL, use the reported text verbatim
      instead of `AssertMql()` and say in the comment where it comes from.
- [ ] Run the same three on `Debug EF8` and `Debug EF9`; add `#if` only if a baseline genuinely differs.
- [ ] Commit: `EF-366: the three wrong-data GroupBy spec cases decline instead of returning wrong rows`.

---

### Task 7: Spec suite — the three drift-only GroupBy methods

Independent of the guard: three expectation drifts caused by driver 3.10 alone. Kept in this slice so the
GroupBy class is fully green, but a reviewer can reject this task without touching Tasks 1–6.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs` (L1897–1910, L1923–1936, L2324–2337)

**Interfaces:**
- Consumes: `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(Func<Task>, params Type[])`, `MongoDB.Bson.BsonSerializationException`
- Produces: no API

**Steps:**

- [ ] `GroupBy_with_group_key_access_thru_nested_navigation` (L1897) — the driver-LINQ fallback now **works and
      returns correct data**, so asserting a failure is false. Retarget to `await base.…` and record the real
      MQL. Run it first to capture the emitted pipeline, then write:

```csharp
    public override async Task GroupBy_with_group_key_access_thru_nested_navigation(bool async)
    {
        // Driver 3.10 translates this correctly (it did not at 3.9), so the query now returns correct data and
        // the previous translation-failure expectation is false. The baseline below is the driver-LINQ
        // fallback's real pipeline, captured from the run.
        await base.GroupBy_with_group_key_access_thru_nested_navigation(async);

        AssertMql(
            """
            <PASTE THE EXACT MQL REPORTED BY THE FAILING AssertMql RUN>
            """);
    }
```

      Do **not** invent the baseline: run the method, take the actual text from the `AssertMql` diff, and if it
      differs between EF8/EF9 and EF10, keep the existing `#if EF8 || EF9` split.
- [ ] `GroupBy_with_group_key_being_nested_navigation` (L1923) — still fails, but now *after* a real pipeline
      executed (`FormatException` while deserializing a whole-entity `$group` key), so only the recorded MQL
      drifted. Keep `AssertTranslationFailed`; replace the `OrderDetails.` fingerprint with the real pipeline
      text captured from the run, and note in a comment that the underlying driver behaviour (a whole-entity
      `$group` key deserialized with `BsonClassMapSerializer<T>` rather than the registered serializer) is
      recommended follow-up (ii) in the design spec.
- [ ] `Complex_query_with_group_by_in_subquery5` (L2324) — driver 3.10 changed the *failure type* for this
      unsupported shape from `ExpressionNotSupportedException` to `BsonSerializationException`. Accept the type
      **at this call site only**, not in the file-wide shadow (which would loosen 240+ other assertions):

```csharp
    public override async Task Complex_query_with_group_by_in_subquery5(bool async)
    {
        // Fails: GroupBy issue EF-149. Driver 3.10 changed the failure TYPE for this unsupported shape:
        // ConstantExpressionToAggregationExpressionTranslator now tries to BSON-serialize the un-inlined
        // MongoQuery<Customer,Customer> subquery constant and dies in BsonClassMap.Freeze() with a duplicate
        // element name, where 3.9 threw ExpressionNotSupportedException. Per AGENTS.md an exception-type change
        // on an UNSUPPORTED operation is not a breaking change, so the accepted-type list is widened here — at
        // this call site only, so the file-wide shadow stays strict for every other case. The driver-side
        // ugliness (a translation error surfacing as a serialization error) is recommended follow-up (i) in
        // docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §5.
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.Complex_query_with_group_by_in_subquery5(async),
            typeof(ArgumentException), typeof(FormatException), typeof(BsonSerializationException));

        AssertMql(
            """
            <PASTE THE EXACT MQL REPORTED BY THE FAILING AssertMql RUN, or use AssertMql() if none>
            """);
    }
```

      Add `using MongoDB.Bson;` to the file if `BsonSerializationException` is not already resolvable.
- [ ] Run the whole `NorthwindGroupByQueryMongoTest` class on EF10 and confirm 0 failures.
- [ ] Run it on `Debug EF8` and `Debug EF9`; adjust `#if` blocks only where a measured baseline differs.
- [ ] Commit: `EF-366: re-target the three driver-3.10 expectation drifts in the GroupBy spec suite`.

---

### Task 8: Un-skip and retarget the six CSHARP-6017 Join tests

Their `Skip` reason claims "returning wrong results", which the provider now refuses to do — leaving them would
put a false statement in the suite and keep an XUnit `Skip` the project's spec-suite convention forbids.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindJoinQueryMongoTest.cs` (L159–162, L169–172, L184–187, L275–278, L489–492, L701–704)

**Interfaces:**
- Consumes: `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(Func<Task>, params Type[])`
- Produces: no API

**Steps:**

- [ ] Replace each of the six `[ConditionalTheory(Skip = "CSHARP-6017…")]` attributes with a plain
      `[ConditionalTheory]` and retarget the body. `NativeTranslationNotSupportedException` derives from
      `Exception`, **not** `InvalidOperationException`, so EF Core's base `AssertTranslationFailed` will not
      accept it — call `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync` at the call site rather than
      adding a `protected new static AssertTranslationFailed` shadow to this file (a shadow would silently
      loosen the ~10 existing `AssertTranslationFailed` call sites here). Pattern, for
      `Join_customers_orders_with_subquery_with_take` (L159):

```csharp
    [ConditionalTheory]
    [MemberData(nameof(IsAsyncData))]
    public override async Task Join_customers_orders_with_subquery_with_take(bool async)
    {
        // Declines: the join's inner is `(Orders.OrderBy(OrderID).Select(o2)).Take(5)` — a self-paging inner,
        // which driver 3.10 mistranslates (CSHARP-6017) by folding $sort/$limit into the correlated $lookup
        // sub-pipeline. The provider hard-declines rather than return the driver's wrong rows, so this is now a
        // translation failure rather than a skip. Its non-Take sibling
        // (Join_customers_orders_with_subquery) is unaffected and still asserts real data.
        // TODO(CSHARP-6017): on driver fix, revert to `await base.…` with a real MQL baseline.
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.Join_customers_orders_with_subquery_with_take(async));

        AssertMql();
    }
```

      Apply the same shape to the other five, each with its own one-line description of its inner:
      `Join_customers_orders_with_subquery_anonymous_property_method_with_take` — `(Orders.OrderBy.Select(new{o2})).Take(5)`;
      `Join_customers_orders_with_subquery_predicate_with_take` — `(Orders.Where(id>0).OrderBy.Select(o2)).Take(5)`;
      `GroupJoin_simple_subquery` — `Orders.OrderBy(OrderID).Take(4)`;
      `GroupJoin_Subquery_with_Take_Then_SelectMany_Where` — `Orders.OrderBy(OrderID).Take(100)`;
      `GroupJoin_customers_employees_subquery_shadow_take` — `Employees.OrderBy(City).Take(5)`.
- [ ] Run the six on EF10, then EF8 and EF9:
      `dotnet test … --filter "FullyQualifiedName~NorthwindJoinQueryMongoTest.Join_customers_orders_with_subquery_with_take|…"` (one filter per method is fine).
- [ ] **Contingency, and it must be honoured rather than worked around:** if any of the six still *executes*
      (i.e. `AssertNativeTranslationFailedAsync` reports "the query threw Xunit…EqualException"), the guard does
      not reach that shape — EF navigation expansion rewrote it into a `SelectMany` rather than a
      `Queryable.GroupJoin`. For that method only, restore `[ConditionalTheory(Skip = …)]` with the reason
      rewritten to say **why**: `Skip = "CSHARP-6017: driver 3.10 folds an uncorrelated Take join inner into the
      correlated $lookup sub-pipeline, returning wrong results. Not reached by the provider's paged-inner guard —
      EF rewrites this shape to SelectMany, which never enters TranslateJoinCore. EF-366 follow-up."` Record
      each such method in the PR description. Do not leave any test asserting something untrue.
- [ ] Also re-baseline the two tautology tests in this file, whose asserted exception type changes from
      `ExpressionNotSupportedException` to `NativeTranslationNotSupportedException` (their inner is
      `Orders.OrderBy(OrderID).Take(10)`): `Inner_join_with_tautology_predicate_converts_to_cross_join` (L494)
      and `Left_join_with_tautology_predicate_doesnt_convert_to_cross_join` (L508). Replace the
      `Assert.Contains("Expression not supported", (await Assert.ThrowsAsync<ExpressionNotSupportedException>(…)).Message)`
      form with `await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(() => base.…(async));` plus the
      MQL baseline the run reports (expected `AssertMql()` — the decline is at compile time, so the previous
      `Customers.` empty-pipeline fingerprint is no longer emitted). Keep the existing `#if EF8 || EF9` split in
      the second one if the measured behaviour still differs by version, and preserve the
      `// Fails: Multiple query roots issue EF-220` note — that is a *different* pre-existing reason and it is
      still true.
- [ ] Commit: `EF-366: un-skip the six CSHARP-6017 join tests and retarget them to a translation failure`.

---

### Task 9: `Reverse_in_join_inner_with_skip` re-baseline, and the docs

The third exception-type re-baseline, plus the two documentation changes the design spec commits to.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs` (L1428–1445)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (L39)
- Modify: `BREAKING-CHANGES.md` (the unreleased "Breaking changes in 8.5.0 / 9.2.0 / 10.1.0" section)

**Interfaces:**
- Consumes: nothing new
- Produces: documentation only

**Steps:**

- [ ] `Reverse_in_join_inner_with_skip` — the inner is `Orders.OrderByDescending(OrderID).Skip(2).Reverse()`, so
      it now declines. Replace the EF10 `Assert.ThrowsAsync<ExpressionNotSupportedException>` arm with
      `await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(() => base.Reverse_in_join_inner_with_skip(async));`
      and the MQL baseline the run reports; keep the `#if EF8 || EF9` arm if its measured behaviour is unchanged.
      Add a one-line comment naming the paged inner and CSHARP-6017. Run all three configurations. Note its
      sibling `Reverse_in_join_outer_with_take` (paging on the **outer**) must stay exactly as it is — run it too
      and confirm.
- [ ] Correct `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md:39`. The final sentence currently reads:

      > **Join-then-group** (an aggregate over a join result) falls back **correctly** and is unaffected — do not
      > conflate the two orderings.

      Driver 3.10 falsified that premise. Replace it with:

      > **Join-then-group** (an aggregate over a join result) falls back **correctly** *provided the join's inner
      > sequence does not page itself* — do not conflate the two orderings. A join (in either ordering) whose
      > **inner sequence applies `Skip`/`Take` to itself** is a SECOND, independent hard-decline: driver 3.10
      > folds that uncorrelated inner's `$sort`/`$skip`/`$limit` into the **correlated** `$lookup` sub-pipeline,
      > where it runs per-outer-row over a ≤1-document key match, so the fallback returns **silently wrong** rows
      > (measured: 0 rows where 453 is correct; the spec's `Join_complex_GroupBy_Aggregate` is join-then-group and
      > returned 0 rows instead of 29). `MongoSelectDefinition.MarkPagedJoinInnerFallbackUnsafe()`, set in the
      > QMTEV `TranslateJoinCore` when `innerQueryExpression.Select.HasPagingAnywhere`, joins
      > `IsGroupByFallbackUnsafe` under the union signal `IsFallbackWrongData` that the gate reads. **Explicit
      > `MongoQueryMode.DriverLinq` still executes it**, same opt-in and same caveat. This one is a *temporary*
      > guard for driver bug **CSHARP-6017** — see the removal checklist in
      > `docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md` §2.6; the tripwire
      > test `NativeJoinPagedInnerDeclineTests.Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017`
      > fails when the driver is fixed. Separately, a wrong-data verdict reached on a join's **inner** select is
      > now propagated to the outer one (`PropagateFallbackWrongDataFrom`), because the gate only reads the
      > outermost query expression — that propagation is permanent and is what makes
      > `Join_GroupBy_Aggregate_in_subquery` decline.

      Keep the whole note on one logical line, matching the file's existing single-line `>` blockquote style.
- [ ] Add the `BREAKING-CHANGES.md` entry to the existing unreleased section, following the house structure
      (`#### Old behavior` / `#### New behavior` / `#### Why` / `#### Mitigations`):

```markdown
### A join whose inner sequence applies `Skip`/`Take` to itself now throws instead of returning wrong results

#### Old behavior

`Skip`/`Take` applied to the **inner** sequence of a `Join`, `GroupJoin` or `LeftJoin` — for example
`orders.Join(customers.OrderBy(c => c.City).Skip(10).Take(50), o => o.CustomerId, c => c.Id, (o, c) => new { … })` —
behaved differently depending on which version of the MongoDB C# driver was resolved. With driver 3.9 (the version
the previous release pinned) the query threw a driver `ExpressionNotSupportedException`. With driver 3.10 — which
the previous release's `MongoDB.Driver (>= 3.9.0)` dependency permits, and which this release pins — the query
**ran and returned silently wrong rows**: the driver's LINQ provider folds the uncorrelated inner's
`$sort`/`$skip`/`$limit` into the correlated `$lookup` sub-pipeline, where they apply per outer document over a
key match of at most one document instead of once over the whole inner sequence. Measured against the Northwind
data set, one such query returned 0 rows where 453 is correct, and another returned 830 rows where 181 is correct.

#### New behavior

The provider recognizes the shape at translation time and throws, rather than executing a query it knows the
driver mistranslates. Explicit `UseQueryMode(MongoQueryMode.DriverLinq)` still executes it, unchanged — that mode
is the documented opt-in to driver-LINQ execution and carries this caveat.

Paging elsewhere is unaffected and still works: `Skip`/`Take` on the **outer** sequence, `Skip`/`Take` applied
**after** the join, and a filtered `Include` such as `Include(c => c.Orders.OrderBy(o => o.Date).Take(5))` are all
correct and are not declined.

#### Why

The underlying defect is in the MongoDB C# driver (CSHARP-6017), not in the provider. Until it is fixed, the only
two options for this shape are a clean failure or silently wrong data. A clean failure is the safe one. The guard
is temporary and will be removed when the driver stops folding.

#### Mitigations

Materialize the paged inner sequence first, then join against it:

```c#
var page = db.Customers.OrderBy(c => c.City).Skip(10).Take(50).ToList();
var result = db.Orders.Join(page, o => o.CustomerId, c => c.Id, (o, c) => new { o, c }).ToList();
```

Or, if you need the previous behavior including its incorrect results, opt in explicitly:

```c#
options.UseMongoDB(connectionString, databaseName, b => b.UseQueryMode(MongoQueryMode.DriverLinq));
```
```

- [ ] Do **not** add an entry for the GroupBy fallback exposure the branch itself introduced: it has never
      shipped, so per the `AGENTS.md` rubric it is not a break. It belongs in the PR description.
- [ ] Commit: `EF-366: correct the Query AGENTS.md decline note and log the behaviour change`.

---

### Task 10: Validation

**Files:** none modified. Artifacts retained under
`/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/323eba4f-8228-4bc2-ae7f-40ed6677ddfe/scratchpad/specsweep-EF-366/`.

**Interfaces:**
- Consumes: the retained baselines `…/scratchpad/specsweep-365391f/native.trx` and `…/nativeonly.trx`, and the
  parser `…/scratchpad/scripts/parsetrx.py`
- Produces: `…/scratchpad/specsweep-EF-366/{native,nativeonly}.trx` + `.log`, and a by-name diff report

**Steps:**

- [ ] With `MONGODB_URI` and `ATLAS_URI` **unset**, run the three-version build + test:
      invoke the `/test-all` skill (`.claude/skills/test-all/`). All three configurations must be green.
- [ ] EF10 spec sweep in default `Native` mode, retaining the trx:

```bash
SWEEP="/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/323eba4f-8228-4bc2-ae7f-40ed6677ddfe/scratchpad/specsweep-EF-366"
mkdir -p "$SWEEP"
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build \
  --logger "trx;LogFileName=$SWEEP/native.trx" > "$SWEEP/native.log" 2>&1
```

- [ ] EF10 spec sweep in `NativeOnly` mode (the same switch the baseline sweep used — read
      `…/scratchpad/specsweep-365391f/nativeonly.log` for the exact invocation and reuse it verbatim so the
      comparison is apples-to-apples; it is driven by `MONGODB_EF_NATIVE_ONLY=1`), retaining
      `$SWEEP/nativeonly.trx` and `$SWEEP/nativeonly.log`.
- [ ] Compare **by test name**, not by count, against the retained baselines — a recorded process lesson is that
      a previous sweep kept no artifact and its claim became un-recheckable, and count-only comparisons hide
      offsetting changes:

```bash
SCR="/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/323eba4f-8228-4bc2-ae7f-40ed6677ddfe/scratchpad"
python3 - <<'EOF'
import xml.etree.ElementTree as ET, os
NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
SCR = os.environ.get('SCR')
def results(path):
    out = {}
    for _, el in ET.iterparse(path, events=('end',)):
        if el.tag == NS + 'UnitTestResult':
            out[el.get('testName')] = el.get('outcome')
            el.clear()
    return out
for mode in ('native', 'nativeonly'):
    old = results(f'{SCR}/specsweep-365391f/{mode}.trx')
    new = results(f'{SCR}/specsweep-EF-366/{mode}.trx')
    print(f'=== {mode}: baseline {len(old)} cases, new {len(new)} cases')
    for name in sorted(set(old) | set(new)):
        o, n = old.get(name, '<absent>'), new.get(name, '<absent>')
        if o != n:
            print(f'  {o:>12} -> {n:<12} {name}')
EOF
```

- [ ] Account for **every** line the diff prints. Expected, and nothing else:
      - `Failed -> Passed` for `Join_complex_GroupBy_Aggregate`, `GroupJoin_complex_GroupBy_Aggregate`,
        `Join_GroupBy_Aggregate_in_subquery`, `GroupBy_with_group_key_access_thru_nested_navigation`,
        `GroupBy_with_group_key_being_nested_navigation`, `Complex_query_with_group_by_in_subquery5` (×2 for
        async) — the 12 case-failures this slice fixes.
      - `NotExecuted -> Passed` for the six un-skipped Join tests (×2).
      - `Passed -> Passed` (i.e. absent from the diff) for the three re-baselined exception-type tests — they were
        edited to keep passing.
      - New test names appearing as `<absent> -> Passed`: the nine in `NativeJoinPagedInnerDeclineTests`, the one
        in `CrossCollectionIncludeTests`, and the unit tests are in a different assembly so they will not appear
        here.
      Any **other** transition — in particular any `Passed -> Failed` among the bulk-update `Delete_with_*`
      tests, `GroupJoin_subquery_projection_outer_mixed`, `Projection_when_arithmetic_mixed*`,
      `Where_subquery_anon*`, `SelectMany_mixed`, `OrderBy_SelectMany`, `Tags_on_subquery`,
      `Select_Where_Subquery_Equality`, `Client_Join_select_many` or `Lifting_when_subquery_nested_order_by_*`
      (design spec §1.3a item 3 predicts these are unaffected because their EF-level translation fails inside
      the QMTEV before the gate reads the flag) — is a finding: fix it by re-baselining that test's assertion,
      and record it in the PR description. Do **not** silence it.
- [ ] Confirm no `Passed -> Failed` transition anywhere involves a *data* assertion. The design spec's
      calibration says zero green-and-correct tests should be lost; if one is, stop and escalate — that is the
      condition under which the whole approach was to be reconsidered.
- [ ] Confirm the artifacts are retained: `ls -la "$SWEEP"` shows both `.trx` and both `.log`. Reference the
      path in the PR description.
- [ ] `git status` clean apart from the intended commits; `grep -rn "CSHARP-6017" src tests docs BREAKING-CHANGES.md`
      lists exactly the removal checklist sites (design spec §2.6) and nothing stale. (**Reconciled in the final
      fix wave:** design spec §2.6 previously greped only `src tests docs`, which left `BREAKING-CHANGES.md`
      outside the root even though its entry is one of the sites that has to be decided at removal time — §2.6 now
      greps the same four roots this line does, and states the ship-order rule for that entry.)
- [ ] Commit any re-baselines this task turned up: `EF-366: re-baseline spec assertions surfaced by the sweep`.
