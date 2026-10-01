# Native LINQ Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every known query that returned correct results on `main` via driver LINQ returns the same results on `EF-322c` via native LINQ — `MONGODB_EF_NATIVE_ONLY=1` is green on EF8/EF9/EF10 for every test that doesn't deliberately pin a mode — and the confirmed native regressions (silent wrong data, new loud failures) are fixed.

**Architecture:** Three kinds of work. (1) Test infrastructure so *every* suite (spec **and** functional) can be forced to `NativeOnly` or `DriverLinq`, plus a differential runner that lists "driver passes / native fails" tests mechanically. (2) Fix the eleven confirmed regressions: F1–F10 found by the 2026-10-01 review, plus F11 found by the Phase 0 final review. (3) Close native-translation gaps bucket by bucket — first the 129 implicit-mode tests that fall back today, then the explicit `DeclinesCleanly` pins whose driver oracle is correct (after confirming each against canonical `main`). Finally remove `IsNativeOnly` divergence and gate on a clean NativeOnly run. Issues that are wrong on `main` too are tracked separately, not fixed here.

**Tech Stack:** C# / .NET 8 + 10, EF Core 8/9/10 (build configurations `Debug EF8|EF9|EF10`), MongoDB C# driver 3.x, xUnit (plain `Assert.*`), Testcontainers `mongodb-atlas-local`.

**Spec / evidence:** `docs/superpowers/findings/2026-10-01-native-parity/` — read `BRIEF.md` first. Per-bucket designs with full C#: `g1-tph.md`, `g2-converters.md`, `g3-owned.md`, `g4-projection.md`, `g5-collections.md`, `g6-joins.md`, `g7-spec.md`. Pin classifications: `pins1.tsv`, `pins2.tsv`, `pins3.tsv`. Main-vs-branch test changes: `mainworks.md`. Gap inventory: `nativeonly-gaps.tsv`. Probe results: `probes-branch.txt`, `probes-main.txt`, probe source `ReviewProbeTests.cs.txt`. Main-side tracking: `main-issues.md`. When a task says "full code in gN §X", that section is the authoritative design; this plan carries the decisions, file targets, tests and verification.

## Global Constraints

- Branch: all work on `EF-322c` (check `git branch --show-current` before each task). No git worktrees. Commit subjects start `EF-322: `; other ticket keys go in the body. Never force-push; squash a finished slice to one commit with a `-presquash` backup branch, ff onto `EF-322c`, push only when asked.
- Canonical `main` = `upstream/main` (`dec7e26f` at plan time; `git fetch upstream main` first). Local `main` is stale — never use it as the oracle.
- Tests: run with `MONGODB_URI` and `ATLAS_URI` **unset** (per-process Atlas-local containers). Each subagent uses a unique scratch subdir. All three EF configurations (`Debug EF8`, `Debug EF9`, `Debug EF10`) must pass; EF8/EF9 build `net8.0`, EF10 `net10.0`.
- Any change to dispatch, a gate, an allow-list or a decline predicate is verified by a **full-suite** run (all projects, all three EF versions), not per-class runs (memory: EF-436 lesson).
- Every new guard/fix test must be proven to discriminate **by mutation** (revert the one-line fix, see it fail, restore).
- The gate must call the fix's predicate, never restate it (Query/AGENTS.md; memory: EF-405).
- New native shapes are tested in **three modes**: `NativeOnly` (goes native), `Native` (same results), `DriverLinq` (oracle / unchanged). Use `NativeModeAssert.NativeAndParity` where the driver oracle is correct; hand-computed expectations over **raw `BsonDocument` seeds** (missing / explicit null / empty / populated) where it isn't.
- Preserve file BOMs. New `src/` types annotated for nullability. Version-conditional code uses `#if EF8 || EF9` / `#if !EF8`.
- Spec MQL baselines are field-order-sensitive; regenerate with `EF_TEST_REWRITE_BASELINES=1` scoped by `--filter`, then `git diff`, rebuild, rerun.
- Breaking-change rubric (root AGENTS.md): which path a query takes and the exact MQL are *not* breaks; result changes and new refusals of supported operations are. Record behaviour changes in `BREAKING-CHANGES.md`.

## Review Focus

Inputs the review implies but no single task's tests naturally exercise — each line's test is added to the owning task (noted):

1. **Missing vs explicit-null vs empty-array documents** for every newly-native shape — the oracle must come from raw seeds, because materialization normalizes missing owned collections to `[]` (EF-358). Owning tasks: every Phase 2/3 task (template in Task 0.3).
2. **Parameterized queries executed twice with different values (including null)** — the compiled native plan is cached by shape; any value baked at compile time is a bug (F2, F7). Owning tasks: 1.2, 1.7 and every task that adds a new constant/parameter rendering (template in Task 0.3).
3. **Non-UTC machine time zone** — Local `DateTime` handling (F2, kind read-back). Owning task: 1.2 (runs under `TZ=Pacific/Auckland` and `TZ=America/Los_Angeles`).
4. **Late fallback after the native shaper is committed** (`Native` mode where the native factory declines at build time) — alias/emit/read agreement. Owning tasks: 2.4, 2.5, 3.x alias families (test via explicit `DriverLinq` leg + forced late-decline leg as in `NativeArrayProjectionTests`).
5. **Value-converted / BsonRepresentation properties reaching newly-native operators** — constants must serialize through the right serializer and results read back through the property serializer. Owning tasks: 1.4, 2.2, every Phase 3 family touching comparisons/aggregates.
6. **Correlations on a non-key outer member** (`db.Orders.Where(o => o.CustomerId == c.Code)` where `Code` is not the principal key, both operand orders, `Code != Id` in the seed). Every recognizer that turns a correlated `DbSet` subquery into a navigation must check *which* outer member is compared, not only that one side is rooted in the outer parameter (F11). Owning tasks: 1.11 and any task that adds or widens a correlation recognizer (2.6, 3.x join/ref-count families).

## Owner decisions (resolve before the owning task; recommendation is the default if the owner doesn't rule)

| ID | Question | Recommendation | Blocks |
|---|---|---|---|
| D-EF337 | Native admission of **integral-narrowing converters** (long stored as int) in comparisons with CLR-typed constants (restores `Count(p => p.orderFromSun > 4L) == 4`, main-correct). Bridge keeps refusing. | Admit natively (g2 T7). | 2.2.7 |
| D-F10 | Bare projection of a **missing** required scalar (`Select(x => x.Rank)`): main returned `default`, native throws. | Mirror main: missing → `default(T)` on the Projection route only; explicit BSON null still throws; whole-entity reads stay strict (g4 T9). | 1.10 |
| D-F5 | String methods on `[BsonRepresentation(ObjectId)] string`: native returns `[]`, main server-errored. | Decline natively (Native falls back to main's loud error; NativeOnly throws). | 1.5 |
| D-RAND | `Random.Next`/`Guid.NewGuid` predicates (main baked one value; some spec tests passed by coincidence). | Keep refusal; native raises the **same** `InvalidOperationException` itself (g7). Optional later: `$rand`. | 2.7.2 |
| D-CULTURE | `CurrentCulture[IgnoreCase]` `StartsWith/EndsWith/Contains` → ordinal (main parity). | Accept (parity), document as accepted divergence. | 2.7.4 |
| D-TOUPPER | `string.Compare(x, x.ToUpper())` (needs `$toUpper`, ASCII-only). | Keep declining; split the two spec overrides into per-sub-query copies with `AssertTranslationFailed` for sub-queries 2 and 5. | 2.7.3 |
| D-TOSTRING | `DateTime.ToString()` in projections (main pushed `$toString` → ISO, wrong). | Client-evaluate (receiver staged, call re-applied on read) — mirrors `DateTimeOffset.ToString`. | 2.7.6 |
| D-TS | `Min/Max` over default (string-stored) `TimeSpan` — main answered lexicographically (wrong in general). | Decline natively for TimeSpan; record M13 for main. | 1.4 |
| D-NULLSAFE | `p.mainAtmosphere.Count()` in projections: `$size` with `$ifNull` (0 for missing) vs abort like main on ragged arrays. | `nullSafe: true` (main only errored on ragged data). | 2.4.5 |
| D-D7 | Filtered join inner with `Include` (spec `Perform_identity_resolution…`): main EF10 silently dropped the inner `Where`. | Not a parity case (main wrong). Hard-decline as fallback-wrong-data **only if** a probe confirms M22; else leave declining. Add `// Fails` under the NativeOnly branch. | 2.6.8 |
| D-F1 | F1 fix changes row sets in **all** modes (principals without dependent return, matching main). | Approve (restores main behaviour). | 1.1 |
| D-PIN-OWNERRULED | Pins whose tests cite an owner ruling / accepted divergence (e.g. `NativeOwnedCollectionFilteredCountTests.cs:1458`, DI:638 bool→string, GB:1558/1615 zero-part key) | Stay declined; out of scope. | Phase 3 |
| D-DEC-TOSTRING | `decimal.ToString()` inside a join inner `Where` (Decimal128 `$toString` differs for exponent forms). | Keep declining unless main-oracle confirmation (0.4) shows main worked for typical values → then owner rules. | 3.J |

---

## Phase 0 — Infrastructure and oracle

### Task 0.1: `MONGODB_EF_QUERY_MODE` for every suite (spec + functional)

Functional tests construct contexts in ~99 places, so the override is a process-wide **internal default**, not per-call plumbing. Explicit `UseQueryMode(...)` calls (deliberate pins) are unaffected.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Infrastructure/MongoOptionsExtension.cs:232`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/ModuleInitialization.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/MongoTestStore.cs:42-51`
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/MongoSpecTestHelpers.cs:35-36`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/TestQueryMode.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.UnitTests/Infrastructure/MongoOptionsExtensionDefaultQueryModeTests.cs`

**Interfaces:**
- Produces: `internal static MongoQueryMode MongoOptionsExtension.DefaultQueryMode { get; set; }` (default `Native`); `TestQueryMode.Current` (`MongoQueryMode`), `TestQueryMode.IsNativeOnly`, `TestQueryMode.IsDriverLinq`; `TestQueryMode.AssertRefusal<TDefault>(Action)` / `AssertRefusalAsync<TDefault>(Func<Task>)` (asserts `NativeTranslationNotSupportedException` under NativeOnly, `TDefault` otherwise).
- Env contract: `MONGODB_EF_QUERY_MODE=NativeOnly|DriverLinq|Native`; `MONGODB_EF_NATIVE_ONLY=1` stays as an alias for `NativeOnly`.

- [ ] **Step 1: Write the failing unit test**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Infrastructure/MongoOptionsExtensionDefaultQueryModeTests.cs
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

public class MongoOptionsExtensionDefaultQueryModeTests
{
    [Fact]
    public void New_extension_uses_DefaultQueryMode_and_explicit_mode_wins()
    {
        var saved = MongoOptionsExtension.DefaultQueryMode;
        try
        {
            MongoOptionsExtension.DefaultQueryMode = MongoQueryMode.NativeOnly;
            var ext = new MongoOptionsExtension();
            Assert.Equal(MongoQueryMode.NativeOnly, ext.QueryMode);
            Assert.Equal(MongoQueryMode.DriverLinq, ext.WithQueryMode(MongoQueryMode.DriverLinq).QueryMode);
        }
        finally
        {
            MongoOptionsExtension.DefaultQueryMode = saved;
        }
    }
}
```

- [ ] **Step 2: Run it — expect compile failure (`DefaultQueryMode` missing)**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → error CS0117.

- [ ] **Step 3: Implement**

```csharp
// MongoOptionsExtension.cs — replace the QueryMode property initializer
/// <summary>Process-wide default for <see cref="QueryMode"/>; test infrastructure only (InternalsVisibleTo).</summary>
internal static MongoQueryMode DefaultQueryMode { get; set; } = MongoQueryMode.Native;

public MongoQueryMode QueryMode { get; private set; } = DefaultQueryMode;
```

```csharp
// FunctionalTests/Utilities/TestQueryMode.cs
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

internal static class TestQueryMode
{
    internal static MongoQueryMode Current { get; } = Resolve();
    internal static bool IsNativeOnly => Current == MongoQueryMode.NativeOnly;
    internal static bool IsDriverLinq => Current == MongoQueryMode.DriverLinq;

    internal static MongoQueryMode Resolve()
        => Environment.GetEnvironmentVariable("MONGODB_EF_NATIVE_ONLY") == "1"
            ? MongoQueryMode.NativeOnly
            : Enum.TryParse<MongoQueryMode>(Environment.GetEnvironmentVariable("MONGODB_EF_QUERY_MODE"), out var m)
                ? m
                : MongoQueryMode.Native;

    internal static void AssertRefusal<TDefault>(Action query) where TDefault : Exception
    {
        if (IsNativeOnly) Assert.Throws<NativeTranslationNotSupportedException>(query);
        else Assert.Throws<TDefault>(query);
    }

    internal static async Task AssertRefusalAsync<TDefault>(Func<Task> query) where TDefault : Exception
    {
        if (IsNativeOnly) await Assert.ThrowsAsync<NativeTranslationNotSupportedException>(query);
        else await Assert.ThrowsAsync<TDefault>(query);
    }
}
```

```csharp
// FunctionalTests/ModuleInitialization.cs — append inside ModuleInitializer()
MongoDB.EntityFrameworkCore.Infrastructure.MongoOptionsExtension.DefaultQueryMode = Utilities.TestQueryMode.Current;
```

Spec: in `MongoTestStore.AddProviderOptions`, compute the mode with the same resolution (copy `Resolve()` into a spec-side `SpecQueryMode` helper — the projects don't reference each other) *[superseded in the Phase 0 final-fix wave: the spec project does reference FunctionalTests, so `SpecQueryMode` was deleted and both suites use the strict `TestQueryMode.Resolve`, which throws on unrecognised values and on an alias/mode conflict]* and call `UseQueryMode(mode)` whenever it isn't `Native`; make `MongoSpecTestHelpers.IsNativeOnly` read that helper. Also set `MongoOptionsExtension.DefaultQueryMode` from a spec `[ModuleInitializer]` so contexts that bypass `MongoTestStore` are covered.

- [ ] **Step 4: Run the unit test → PASS.** Then run the functional suite once with `MONGODB_EF_QUERY_MODE=NativeOnly` (EF10) and confirm the failure set matches the FunctionalTests rows of `nativeonly-gaps.tsv` (≈93 methods; differences must be explained in the commit body).
- [ ] **Step 5: Commit** — `EF-322: honor MONGODB_EF_QUERY_MODE / MONGODB_EF_NATIVE_ONLY in all test suites`

### Task 0.2: Differential parity runner

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/TestMqlLoggerFactory.cs:44` (env-gated skip)
- Create: `tests/tools/native-parity-diff.sh`, `tests/tools/native-parity-diff.py`
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` (document the runner)

**Interfaces:** Produces `tests/tools/native-parity-diff.sh <EF8|EF9|EF10> <outdir> [--gate]` (`--gate` added in the final-fix wave: exit nonzero on any regress entry) → `<outdir>/<ver>-<project>-regress.txt` (driver pass → native fail) and `-improve.txt`.

- [ ] **Step 1:** In `AssertBaseline`, first line: `if (Environment.GetEnvironmentVariable("MONGODB_EF_SKIP_MQL_ASSERTIONS") == "1") return;` (differential runs only — MQL legitimately differs between paths).
- [ ] **Step 2:** Write the shell script (no-build; requires a prior build):

```bash
#!/usr/bin/env bash
# Usage: tests/tools/native-parity-diff.sh EF10 /path/to/outdir
set -euo pipefail
v="$1"; out="$2"; mkdir -p "$out/trx"
unset MONGODB_URI ATLAS_URI
export MONGODB_EF_SKIP_MQL_ASSERTIONS=1
for m in DriverLinq NativeOnly; do
  for p in SpecificationTests FunctionalTests; do
    MONGODB_EF_QUERY_MODE=$m dotnet test "tests/MongoDB.EntityFrameworkCore.$p/MongoDB.EntityFrameworkCore.$p.csproj" \
      -c "Debug $v" --no-build --logger "trx;LogFileName=$v-$m-$p.trx" --results-directory "$out/trx" > "$out/$v-$m-$p.log" 2>&1 &
  done
done
wait
python3 "$(dirname "$0")/native-parity-diff.py" "$out" "$v"
```

`native-parity-diff.py`: parse both TRX files per project (namespace `http://microsoft.com/schemas/VisualStudio/TeamTest/2010`, `UnitTestResult/@testName`, `@outcome`, `Output/ErrorInfo/Message`), write `regress` (DriverLinq `Passed` ∧ NativeOnly `Failed`) and `improve` (the reverse) lists with the failure message, and print counts. (Reference implementation: the review's `cmp.py`, reproduced in the commit.)

- [ ] **Step 3:** Run for EF10; the regress list must equal the EF10 rows of `nativeonly-gaps.tsv` (spec 10 runs + functional ≈109 runs). Note: spec tests branch on `MongoSpecTestHelpers.IsNativeOnly`; in the differential both runs set the env var, so a test with a divergent `IsNativeOnly` branch passes in NativeOnly by construction — those are found by Task 4.1, not by this runner.
- [ ] **Step 4: Commit** — `EF-322: add native/driver differential test runner`

### Task 0.3: Shared parity-test template (documentation + helper)

**Files:** Modify `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/NativeModeAssert.cs`; Modify `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (testing section).

- [ ] Add `NativeModeAssert.NativeAndExpected<T>(Func<MongoQueryMode, List<T>> run, List<T> expected)` — runs NativeOnly, Native and DriverLinq; asserts NativeOnly == Native == `expected`; asserts DriverLinq == `expected` **unless** `driverKnownWrong: true` is passed (then asserts DriverLinq != expected, documenting the main bug ID in a comment).
- [ ] Add `NativeModeAssert.TwiceWithDifferentValues<T>(Func<object?, List<T>> runWithParam, object? first, List<T> expectedFirst, object? second, List<T> expectedSecond)` for Review Focus #2 (same context type → same compiled query).
- [ ] Document the ragged-seed rule (missing / null / empty / populated rows via raw `BsonDocument` inserts) in Query/AGENTS.md.
- [ ] Commit — `EF-322: parity test helpers for expected-oracle and re-execution checks`

### Task 0.4: Confirm in-scope pins against canonical `main`

The pin classifications judged "driver correct" against the **branch's** driver path, which includes bridge fixes not on `main` (e.g. EF-218 `TimeOfDay` rewrite). Before any Phase 3 family is implemented, confirm its representative queries returned correct results on `dec7e26f`.

**Files:** none committed (scratch only); output appended to `docs/superpowers/findings/2026-10-01-native-parity/pins-main-confirmation.md` (committed).

- [ ] **Step 1:** `git fetch upstream main && git archive upstream/main | tar -x -C <scratch>/main-oracle` (never a worktree).
- [ ] **Step 2:** For each Phase 3 family (table in Phase 3), write one probe per representative pin into `<scratch>/main-oracle/tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ZzParityOracle.cs`, using the pattern of `ReviewProbeTests.cs.txt` (a `Record(name, run)` helper that writes outcome text to a file; a `ProbeContext` per model; raw `BsonDocument` seeds copied from the pin's test). Remove `UseQueryMode` calls (main has no query mode).
- [ ] **Step 3:** Build `Debug EF10` in the export and run `--filter ZzParityOracle`. For each family record: **WORKED** (matches the hand-computed expected result), **THREW**, or **WRONG** (add to `main-issues.md`).
- [ ] **Step 4:** Mark each Phase 3 family *in scope* only if ≥1 representative WORKED; demote the rest to "optional coverage" in the confirmation file.
- [ ] **Step 5:** Also probe M22, M25, M11, M12 and the D-F10 anonymous spelling `Select(x => new { x.Rank })` on main, and update `main-issues.md`.
- [ ] **Step 6: Commit** the confirmation doc — `EF-322: confirm parity scope against upstream main`

---

## Phase 1 — Confirmed regressions (F1–F11)

Probe evidence for F1–F10: `probes-branch.txt` vs `probes-main.txt`. F11: `main-issues.md` (M12 probe note) and `.superpowers/sdd/2026-10-01-native-linq-parity/final-fix-report.md`. Each task adds its probe as a permanent functional test.

### Task 1.1: F1 — principal-side reference `Include` drops principals without a dependent (all modes)

Root cause (g6 §F1): EF emits an inner `Join` only for dependent-side required navs (`NavigationExpandingExpressionVisitor.ExpressionVisitors.cs:477`); principal-side references are always LeftJoin. `TryConfirmReferenceIncludeChain` builds a fresh lookup guessing `PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1518-1538`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/PrincipalSideReferenceIncludeTests.cs`

- [ ] **Step 1: Failing tests** — model `Customer{int Id; string Name; Address? Address}`, `Address{int Id; int CustomerId; string City; Customer? Customer}`, `HasOne(c => c.Address).WithOne(a => a.Customer).HasForeignKey<Address>(a => a.CustomerId)`, separate collections; context options pattern from `Ef373InterleavedPagingTests` (`UseMongoDB(..., o => o.UseQueryMode(mode))` + per-collection model cache key). Raw seeds: customers `{_id:1,Name:"with"}`, `{_id:2,Name:"without"}`; addresses `{_id:10,CustomerId:1,City:"Paris"}` (+ for the dependent control, `{_id:11,CustomerId:99,City:"Nowhere"}`).

```csharp
[Theory]
[InlineData(MongoQueryMode.Native)] [InlineData(MongoQueryMode.NativeOnly)] [InlineData(MongoQueryMode.DriverLinq)]
public void Include_principal_side_reference_with_required_dependent_fk_keeps_principals_without_a_dependent(MongoQueryMode mode)
{
    using var db = CreateContext(mode);
    Assert.Equal(["with:Paris", "without:<none>"],
        db.Customers.Include(c => c.Address).OrderBy(c => c.Id).ToList()
            .Select(c => $"{c.Name}:{c.Address?.City ?? "<none>"}").ToList());
}
```
Also: the same with `AsNoTracking()`; dependent-side control `Addresses.Include(a => a.Customer)` drops the dangling `CustomerId: 99` row (inner join, must keep passing); optional-FK dependent control (`int? CustomerId`) keeps the row with a null nav; regression pins `Select(c => c.Address!.City)` → `["with:Paris","without:<null>"]`, `Where(c => c.Address == null)` → `["without"]`, `Where(c => c.Address != null)` → `["with"]`.

- [ ] **Step 2:** Run → the first test FAILS in all three modes (`["with:Paris"]`).
- [ ] **Step 3: Implement** (register the recorded join's own lookup, which carries the real `IsLeftOuter`):

```csharp
// TryConfirmReferenceIncludeChain — replace the else-branch that builds a new LookupExpression
else
{
    var join = mongoQueryExpression.Joins.FirstOrDefault(j => j.Navigation == navigation && j.Lookup is { ForceUnwind: true });
    if (join is null) return false;
    if (join.Lookup!.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal)) return false;
    newLookups.Add(join.Lookup);   // PreserveNullAndEmptyArrays == join.IsLeftOuter
}
```
- [ ] **Step 4:** Tests PASS in all modes; mutation: restore the old line → test 1 fails in all modes. Expected MQL: `{$lookup:{from:"Addresses",localField:"_id",foreignField:"CustomerId",as:"_lookup_Address"}},{$unwind:{path:"$_lookup_Address",preserveNullAndEmptyArrays:true}}`.
- [ ] **Step 5:** Full suite EF8/EF9/EF10 (Include admission is shared). Add a `BREAKING-CHANGES.md` note only if a released version had the bug (check `gh release list`; the bug is branch-only → no entry).
- [ ] **Step 6: Commit** — `EF-322: principal-side reference Include keeps principals without a dependent`

### Task 1.2: F2 — clock members evaluated once at compile time; Local `Now` relabeled UTC (+ spec `Where_datetime_*`)

Design: g7 §"F2 and the clock rows". A maximal closed subtree containing `DateTime.Now/UtcNow/Today` or `DateTimeOffset.Now/UtcNow`, typed `bool/bool?/DateTime/DateTime?/DateTimeOffset`, becomes a **per-execution** `MongoParameterExpression` with a `RuntimeEvaluator`; values serialize through the property serializer (Local → UTC, as the driver did).

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/RuntimeClock.cs` (`ContainsClock(Expression)`, `CompileEvaluator(Expression, IReadOnlyDictionary<string, object?> parameters)`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:356-358` (`TryTranslateDateAdd` receiver), `:385-415` (`TryEvaluateClosedSubtree` declines on `RuntimeClock.ContainsClock`; delete the `SpecifyKind` relabel for evaluated values), `TranslateNode` top + `TranslateValue` (`TryCreateRuntimeClockParameter`)
- Modify: `MongoParameterExpression` (add `RuntimeEvaluator`), `MongoValueRenderer.RenderValue` (register evaluator in `PlaceholderTable`), `MongoPipelineFactory.Build` (both overloads: wrap parameter values in `RuntimeParameterValues` that evaluates each evaluator once per Build)
- Modify tests: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NonDeterministicFunctionTests.cs`; spec `NorthwindWhereQueryMongoTest` `Where_datetime_now/today/utcnow`, `Where_datetimeoffset_utcnow` baselines

- [ ] **Step 1: Failing tests** (all `[Theory]` over the three modes):

```csharp
// One helper method => one lambda => one EF compiled-query cache entry.
private static int CountRecent(ClockContext db) => db.Rows.Count(r => r.Created <= DateTime.UtcNow.AddMilliseconds(0));

[Theory] [InlineData(MongoQueryMode.Native)] [InlineData(MongoQueryMode.NativeOnly)] [InlineData(MongoQueryMode.DriverLinq)]
public async Task UtcNow_add_receiver_is_evaluated_per_execution_not_baked_into_the_cached_plan(MongoQueryMode mode)
{
    SeedRaw(new BsonDocument { { "_id", 1 }, { "Created", DateTime.UtcNow.AddMilliseconds(600) } });
    using (var db = Create(mode)) Assert.Equal(0, CountRecent(db));
    await Task.Delay(1200);
    using (var db = Create(mode)) Assert.Equal(1, CountRecent(db));
}

[Theory] [InlineData(MongoQueryMode.Native)] [InlineData(MongoQueryMode.NativeOnly)] [InlineData(MongoQueryMode.DriverLinq)]
public void Local_Now_add_receiver_compares_instants_in_any_time_zone(MongoQueryMode mode)
{
    SeedRaw(new BsonDocument { { "_id", "past" }, { "Created", DateTime.UtcNow.AddMinutes(-30) } },
            new BsonDocument { { "_id", "future" }, { "Created", DateTime.UtcNow.AddMinutes(30) } });
    using var db = Create(mode);
    Assert.Equal(["past"], db.Rows.Where(r => r.Created <= DateTime.Now.AddMinutes(0)).Select(r => r.Id).ToList());
    Assert.Equal(["past"], db.Rows.Where(r => r.Created <= DateTime.Now.AddMinutes(r.Foo * 0)).Select(r => r.Id).ToList());
}
```
Add `NativeOnly` `InlineData` to existing `UtcNow_in_predicate_still_works` / `Now_in_predicate_still_works`; variants for `DateTime.Today.AddDays(-1)` and `DateTimeOffset.UtcNow.AddDays(-1)`.
- [ ] **Step 2:** Run → `UtcNow…` FAILS under Native/NativeOnly (second count 0); `Local_Now…` FAILS under Native/NativeOnly when `TZ=Pacific/Auckland` (run: `TZ=Pacific/Auckland dotnet test … --filter NonDeterministicFunctionTests`).
- [ ] **Step 3:** Implement per g7 (full C# there). Expected MQL: `{$match:{Created:{$lte:<date>}}}` with the date substituted per execution; spec `Where_datetime_*` become `{ "$match" : { "$expr" : true } }` (regenerate baselines; remove their `IsNativeOnly` branches if any).
- [ ] **Step 4:** PASS in all modes under `TZ=UTC`, `TZ=Pacific/Auckland`, `TZ=America/Los_Angeles`. Mutation: re-enable compile-time evaluation → staleness test fails.
- [ ] **Step 5:** Full suite ×3 EF versions. **Commit** — `EF-322: evaluate clock members per execution in native plans`

### Task 1.3: F3 — non-nullable computed projection over null/missing reads 0 (driver threw)

Design: g4 §T7 (fixes (a) + (b); (c) deferred).

**Files:** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs:~1031` (`WalkNullBehindNonNullableType`), `NativeProjectionBinder.cs:~988` (`ClassifyNonNullableValueRead`); tests in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeNullableMemberTests.cs` (reuse `SeedRagged`: r2 Score null, r3 Score missing).

- [ ] **Step 1: Failing tests**

```csharp
public static TheoryData<string> NullableOperandShapes => new() { "add", "mul", "div", "abs", "cond" };

[Theory] [MemberData(nameof(NullableOperandShapes))]
public void Non_nullable_computed_leaf_over_null_or_missing_operand_throws(string shape)
{
    var collection = SeedRagged(nameof(Non_nullable_computed_leaf_over_null_or_missing_operand_throws) + shape);
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(collection, mode);
        var q = db.Entities.AsNoTracking().OrderBy(x => x.Title);
        var ex = Assert.Throws<InvalidOperationException>(() => (shape switch
        {
            "add" => q.Select(x => x.Score!.Value + 1),
            "mul" => q.Select(x => (int)x.Score! * 2),
            "div" => q.Select(x => x.Score!.Value / 2),
            "abs" => q.Select(x => Math.Abs(x.Score!.Value)),
            _ => q.Select(x => x.Rank > 1 ? x.Score!.Value : 0),
        }).ToList());
        Assert.Contains("Nullable object must have a value", ex.Message);
    }
}
```
Unchanged-behaviour facts: `(x.Score ?? 0) + 1` → `[11,1,1,4]`; `x.Score != null ? x.Score.Value + 1 : 0` → `[11,0,0,4]` (all three modes). `Select(x => x.Score!.Value + 1).Distinct()` → `NativeModeAssert.DeclinesCleanly`.
- [ ] **Step 2:** FAIL (native returns `[11,0,0,4]`).
- [ ] **Step 3:** Implement (g4 §T7 code): field-leaf arm `MongoFieldExpression or MongoOuterFieldExpression when dateParts => new(MayBeNullUnlessProven(node, nonNull), false)`; `ClassifyNonNullableValueRead` returns `Plain` when the top node is itself a bare field. Do **not** flag non-nullable fields.
- [ ] **Step 4:** PASS; mutation each arm. Full suite ×3 (spec baselines that expected 0 may flip — each flip must be justified as main-throw parity).
- [ ] **Step 5: Commit** — `EF-322: throw for non-nullable computed projections over null operands`

### Task 1.4: F4 + F8 — `Sum` overflow wraps; `Min/Max` over enum/DateOnly/char-like types throw `InvalidCastException`

Design: g2 §T1 (steps 1–2 only here; step 3 converted-operand gate is Task 2.2.1).

**Files:** `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:277-279, ~890, 923-929`; Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeAggregateReadBack.cs`; Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeAggregateReadBackTests.cs` (code in g2 §T1).

- [ ] **Step 1:** Failing tests: `MinMax_reads_back_through_property_serializer` theory (enum, DateOnly, char, `Select(e => e.Level).Max()`), `Sum_int_overflow_throws_OverflowException` (3 modes; seed `int.MaxValue, int.MaxValue, 1`), `Sum(long)` overflow → `OverflowException`, `Sum(int?)` overflow. `Max(TimeSpan)`: per D-TS assert `NativeModeAssert.DeclinesCleanly`.
- [ ] **Step 2:** FAIL (InvalidCastException / wrapped value).
- [ ] **Step 3:** `checked` narrowing; `NativeAggregateReadBack.FindOperandProperty` + `BsonSerializerFactory.CreateTypeSerializer(readProperty)` for Min/Max read-back; `DeserializeKindAwareScalar` tolerant cast; decline Min/Max over default-serialized `TimeSpan` in `NativeCardinalityBinder.TryBindAggregate` (call a shared `NativeAggregateReadBack.HasFaithfulServerOrdering(property)` from the gate).
- [ ] **Step 4:** PASS; mutation (remove `checked`; remove serializer read). Full suite ×3.
- [ ] **Step 5: Commit** — `EF-322: checked Sum narrowing and serializer-aware Min/Max read-back`

### Task 1.5: F5 — string methods on an ObjectId-represented string silently return `[]`

Design: g5 §F5 (decision D-F5: decline).

**Files:** Create shared `MongoExpressionTranslator.IsRegexSearchableString(IReadOnlyProperty)` (ClrType string AND (no BsonRepresentation OR representation String)); call it at the 8 gates: `MongoExpressionTranslator.cs:998, :1039`, `.CaseMapping.cs:47`, `.Like.cs:68`, `.Regex.cs:88, :106, :128`, `.StringEquals.cs:64`. Value converters deliberately **not** gated (M3 parity). Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeRepresentedStringMethodTests.cs`.

- [ ] **Step 1:** Failing test: model `Doc { [BsonRepresentation(BsonType.ObjectId)] string Id; string Code }`; `Where(d => d.Id.StartsWith(prefix))` → NativeOnly throws `NativeTranslationNotSupportedException`; Native and DriverLinq both throw `MongoCommandException` (same as main). Same for `EndsWith`, `Contains`, `ToLower() == x`, `EF.Functions.Like`, `Regex.IsMatch`, `Equals(x, OrdinalIgnoreCase)`.
- [ ] **Step 2:** FAIL (native returns `[]`).
- [ ] **Step 3:** Add predicate + 8 call sites.
- [ ] **Step 4:** PASS; mutation per site (at least StartsWith and Like). Full suite ×3. **Commit** — `EF-322: decline string methods over non-string stored representations`

### Task 1.6: F6 — `Math.Sign/Max/Min` over a nullable operand in a predicate matches null rows (driver threw)

Design: g4 §T8. **File:** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs` (`TryTranslateMath`); add `MongoAggregationExpressionRenderer.MayBeNullOperand` (parameter-type aware).

- [ ] **Step 1:** Failing test (rows `D = null, -1, 5`): `Math.Sign(d.D!.Value) < 0` and `Math.Max(d.D!.Value, 1.0) > 0` → NativeOnly `NativeTranslationNotSupportedException`, Native and DriverLinq `ExpressionNotSupportedException` (main parity). Controls stay native and match in-memory: `Math.Max(d.D ?? 0, 1.0) > 2` → `[5]`; `Math.Sign(nonNullable) < 0`; `Math.Max(x.A, capturedInt)`.
- [ ] **Step 2–4:** implement (`return false` when `!IsNullPropagatingMathFunction(function) && operands.Any(MayBeNullOperand)`), PASS, mutation, full suite.
- [ ] **Step 5: Commit** — `EF-322: decline null-absorbing Math functions over nullable operands`

### Task 1.7: F7 — null parameter vs non-nullable property / null in a `Contains` list → `NullReferenceException`

Design: g5 §T4 (full test suite C# there). **Files:** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/BsonValueSerializer.cs` (add `SerializeNullAware`), `MongoValueRenderer.cs:62-68` (`ToBsonValue`), `MongoPipelineFactory.cs:704, 757-758, 766-767`.

- [ ] **Step 1:** Failing tests (3 modes, expected = driver/main result, parity): `int? p = null; Where(x => x.Rank == p)` → `[c]` (missing matches null, as main); `!=` → `[a,b]`; same for enum/Guid/DateTime/bool/long; `List<int?> ids = [1, null]; Where(x => ids.Contains(x.Rank))` → `[a,c]`; constant list with null; `!Contains`; `minRank == null || x.Rank >= minRank` control. Each parameter test also runs via `NativeModeAssert.TwiceWithDifferentValues(null → expected, 1 → expected)`.
- [ ] **Step 2:** FAIL (NRE).
- [ ] **Step 3:**

```csharp
// BsonValueSerializer
internal static BsonValue SerializeNullAware(IBsonSerializer serializer, object? value)
    => value is null && serializer.ValueType.IsValueType && Nullable.GetUnderlyingType(serializer.ValueType) is null
        ? BsonNull.Value
        : SerializeThroughWriter(serializer, value);
```
Use it at the four sites. MQL: `{Rank:null}`, `{Rank:{$ne:null}}`, `{Rank:{$in:[1,null]}}`.
- [ ] **Step 4:** PASS; mutation. Full suite ×3. Add M8 note (main semantics differ from C#) — already in `main-issues.md`.
- [ ] **Step 5: Commit** — `EF-322: serialize null parameters for non-nullable properties as BSON null`

### Task 1.8: F9 — tuple equality with a `Guid` element → `ArgumentException`

Design: g5 §T5. **Files:** `MongoExpressionTranslator.TupleEquality.cs` (`TryDecomposeTupleOperand`: rebind a constant/parameter element to the `IProperty` at the same position on the other side when the CLR type isn't `BsonValue.Create`-safe; pass `valueType` for whole-tuple parameters); `MongoValueRenderer.RenderValue` (`ArgumentException` → `NativeTranslationNotSupportedException` safety net, so Native falls back).

- [ ] Failing tests (3 modes, parity with main `["abc"]`): `new Tuple<Guid,int>(d.Gid, d.N) == new Tuple<Guid,int>(g, 1)`; enum and `ValueTuple` variants; parameter tuple. Implement, PASS, mutation, full suite. **Commit** — `EF-322: serialize tuple-equality elements through their properties`

### Task 1.10: F10 — bare projection of a missing required scalar (decision D-F10)

Design: g4 §T9. **Files:** `src/MongoDB.EntityFrameworkCore/Storage/BsonBinding.cs` (add `GetScalarProjectionValueAtElement<T>` — missing element → `default(T)`; explicit BSON null → throw as today), `MongoProjectionBindingRemovingExpressionVisitor.cs:~254` (plain-property arm, **only** when `Route == NativeRoute.Projection`).

- [ ] Failing tests (seed `{Title:"c"}` without Rank; 3 modes): `Select(x => x.Rank)` → `[1,2,0]`; `Select(x => new { x.Rank })` → same (confirm main via Task 0.4 first); explicit-null bare read still throws; whole-entity `ToList()` still throws. Implement, PASS, mutation, full suite. **Commit** — `EF-322: missing required scalar in a projection reads default, matching driver LINQ`

### Task 1.11: F11 — correlated-collection matcher binds a non-key outer member as the navigation

Found by the Phase 0 final review (refuting M12). `NativeCorrelationMatcher.TryMatchCorrelatedCollection` (`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCorrelationMatcher.cs:40-81`) checks that one equality side is rooted in the outer parameter and that the other side's property name is the FK, but never that the outer side is the FK's **principal key**. A correlation on any other outer member therefore binds as the navigation and emits `$lookup {localField: "_id", foreignField: "CustomerId"}`: silent wrong data. On `main` the same queries throw.

Branch probe (EF8 and EF10, model below, `Code != Id`; correct answer by `Code`):

| Shape | Correct | NativeOnly | Native | DriverLinq (branch) |
|---|---|---|---|---|
| `from c in Customers from o in db.Orders.Where(o => o.CustomerId == c.Code) select (c.Id, o.Id)` | `1-20, 3-10, 3-11, 3-12` | `1-10, 1-11, 1-12, 2-20` **wrong** | same **wrong** | throws `Unsupported cross-DbSet query` |
| same, reversed operands `c.Code == o.CustomerId` | same | **wrong** (key rows) | **wrong** | throws |
| same, `o.CustomerId == c.Code && o.Id > 10` | `1-20, 3-11, 3-12` (corrected: customer 1 has `Code` 2, so order 20 qualifies) | `1-11, 1-12, 2-20` **wrong** | **wrong** | throws |
| `Select(c => new { c.Id, F = db.Orders.Where(o => o.CustomerId == c.Code).OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault() })` | `1:20, 2:0, 3:10` | `1:10, 2:20, 3:0` **wrong** | **wrong** | throws |
| `Where(c => db.Orders.Where(o => o.CustomerId == c.Code).Count() > 0)` | `[1, 3]` | `[1, 2]` **wrong** | **wrong** | `[1, 2]` **wrong** (the native slot populator registers the count `$lookup` before routing; main's RefCount family threw here) |
| `Select(c => db.Orders.Where(o => o.CustomerId == c.Code).Count())` | `[1, 0, 3]` | `[3, 1, 0]` wrong | wrong | wrong: **main bug M35** (`ResolveCollectionNavigation`, identical on main) |
| `Select(c => db.Orders.Count(o => o.CustomerId == c.Code))`, `Select(c => db.Orders.Where(…).Select(o => o.Id).ToList())` | — | throw `DbSet<Order>() could not be translated` in all modes (EF; same as main) | | |
| key-correlated controls (`o.CustomerId == c.Id`) of the SelectMany / FirstOrDefault / Count shapes | key rows | correct, native | correct | SelectMany/FOD throw; Count correct |

**Main oracle (Task 1.11 Step 2, built and run):** `upstream/main` at `dec7e26f`, Debug EF8/EF9/EF10, same model and raw
seeds. Every shape above except the bare projected count throws `InvalidOperationException`, key-correlated or not:
the SelectMany shapes (incl. nested and nullable-FK) and the correlated `FirstOrDefault` reducer with "The LINQ
expression 'DbSet<…>()' could not be translated", the `Where(… Count() > 0)` shapes with "Unsupported cross-DbSet
query". The bare projected `Where(corr).Count()` returns `[3, 1, 0]` for both the key and the non-key correlation
(M35). No shape returns correct data on main that native would now refuse.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCorrelationMatcher.cs` (`TryMatchCorrelatedCollection`; also the null-guard arm of `TryGetCorrelationEqualitySides`, which accepts `x != null && equality` without checking `x` is the compared key)
- Audit (every caller goes through the matcher; confirm each declines after the fix, add a test per caller): `NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation` (→ `NativeProjectionBinder.TryTranslateProjectedCollectionCount` and `NativeReferenceCollectionCountPredicateBinder.TryTranslate`), `NativeSelectManyBinder.TryBindReferenceNavUnwind` / `TrySplitCorrelation` / `TryBindNestedReferenceNavUnwind` (synthetic level-1 parameter), `NativeProjectionBinder.TryTranslateProjectedCollectionNavigationList` (projected collection-list leaves), `NativeProjectionBinder.TryGetCorrelatedReducerLeaf` (correlated reducer). Re-grep `TryMatchCorrelatedCollection` before starting.
- Not in scope: `MongoProjectionBindingExpressionVisitor.ResolveCollectionNavigation` (driver path, same hole on main = M35; tracked in `main-issues.md`).
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeNonKeyCorrelationTests.cs`

- [ ] **Step 1: Failing tests** — model and seed (raw inserts; `Code` deliberately differs from `Id`). Context pattern from `NativeCorrelatedReducerProjectionTests` (`UseQueryMode(mode)`, per-collection names, `IgnoreCacheKeyFactory`):

```csharp
private class Customer { public int Id { get; set; } public int Code { get; set; } public List<Order> Orders { get; set; } = null!; }
private class Order { public int Id { get; set; } public int CustomerId { get; set; } }
// OnModelCreating: mb.Entity<Customer>().HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
// Seed: customers {Id 1, Code 2}, {Id 2, Code 3}, {Id 3, Code 1}; orders {10, Cust 1}, {11, Cust 1}, {12, Cust 1}, {20, Cust 2}.

public static TheoryData<string> NonKeyShapes => ["SelectMany", "SelectManyReversed", "SelectManyConjunct", "FirstOrDefault", "WhereCount"];

[Theory, MemberData(nameof(NonKeyShapes))]
public void Non_key_correlation_declines_in_NativeOnly(string shape)
{
    using var db = CreateContext(MongoQueryMode.NativeOnly);
    Assert.Throws<NativeTranslationNotSupportedException>(() => Run(db, shape, key: false));
}

[Theory, MemberData(nameof(NonKeyShapes))]
public void Non_key_correlation_in_Native_matches_main(string shape)
{
    // Native falls back; main (and DriverLinq once the slot populator declines) throws for every shape here.
    using var db = CreateContext(MongoQueryMode.Native);
    Assert.Throws<InvalidOperationException>(() => Run(db, shape, key: false));
}

[Theory, MemberData(nameof(NonKeyShapes))]
public void Non_key_correlation_in_DriverLinq_matches_main(string shape)
{
    using var db = CreateContext(MongoQueryMode.DriverLinq);
    Assert.Throws<InvalidOperationException>(() => Run(db, shape, key: false));
}

[Theory]
[InlineData(MongoQueryMode.NativeOnly)] [InlineData(MongoQueryMode.Native)]
public void Key_correlation_stays_native_and_correct(MongoQueryMode mode)
{
    using var db = CreateContext(mode);
    Assert.Equal(["1-10", "1-11", "1-12", "2-20"], Run(db, "SelectMany", key: true));
    Assert.Equal(["1:10", "2:20", "3:0"], Run(db, "FirstOrDefault", key: true));
    Assert.Equal(["1", "2"], Run(db, "WhereCount", key: true));
}

[Fact]
public void Bare_projected_non_key_count_declines_natively_and_falls_back_to_main_behavior()
{
    using (var db = CreateContext(MongoQueryMode.NativeOnly))
        Assert.Throws<NativeTranslationNotSupportedException>(() => BareCount(db));
    // M35: main and DriverLinq return the key-correlated counts [3, 1, 0] (correct: [1, 0, 3]). Parity with main,
    // not correctness; flip this when M35 is fixed on main.
    using var native = CreateContext(MongoQueryMode.Native);
    using var driver = CreateContext(MongoQueryMode.DriverLinq);
    Assert.Equal(BareCount(driver), BareCount(native));
}
// Run(db, shape, key) builds the shape from the probe table with `c.Code` (or `c.Id` when key) and formats rows as above.
```
Also: a nested two-level SelectMany whose level-2 correlation uses a non-key level-1 member (exercises the synthetic-parameter path), and a nullable-FK variant (`int? CustomerId`, which adds EF's `!= null` guard) for both the key control and a non-key guard.

- [ ] **Step 2:** Run → the NativeOnly/Native non-key tests FAIL (wrong rows returned); `WhereCount` also fails under DriverLinq (`[1, 2]`).
- [ ] **Step 3: Implement** — in `TryMatchCorrelatedCollection`, after selecting the single candidate, require the outer side to be a direct property access on `outerParameter` (after `RemoveConvert`; `EF.Property(outer, "x")` included; a nested member such as `c.Address.Code` declines) that resolves, **by `IProperty`, not name**, to the FK's principal key:

```csharp
var outerSide = ReferenceEquals(dependentSide, side1) ? side2 : side1;   // handles either operand order
var outerProperty = TryGetDirectOuterProperty(outerSide, outerParameter, outerEntityType);   // IProperty? via FindProperty
var candidate = candidates[0];
if (outerProperty is null || !ReferenceEquals(outerProperty, candidate.ForeignKey.PrincipalKey.Properties[0]))
    return false;
```
Apply the same principal-key identity to the null-guard arm of `TryGetCorrelationEqualitySides` (the guarded member must be the compared outer key). Callers need no change if they all route through the matcher; if any caller resolves a navigation another way, make it call the same predicate (the gate must call the fix's predicate, never restate it).
- [ ] **Step 4:** PASS. Mutation: remove the principal-key check → the NativeOnly non-key tests return rows and fail; restore. Expected MQL for the key control unchanged (`$lookup {localField:"_id", foreignField:"CustomerId"}`).
- [ ] **Step 5:** Full suite EF8/EF9/EF10 (shared correlation recognizer feeding several dispatch paths) plus `MONGODB_EF_QUERY_MODE=NativeOnly` functional run: no previously-native key-correlated test may start declining. Behaviour change vs released versions: none (main throws for every newly-declining shape) → no `BREAKING-CHANGES.md` entry.
- [ ] **Step 6: Commit** — `EF-322: correlated-collection matcher requires the outer principal key`

### Task 1.12: F3(c) — non-nullable computed projection over a MISSING non-nullable field reads 0 (driver threw)

(Controller-authored task; added to the plan during Phase 1 execution.)

**Problem.** `Select(x => x.Rank + 1)` (also `new { V = x.Rank + 1 }`, `x.Rank * 2`, `Math.Abs(x.Rank)`, conditional over `x.Rank`) where `int Rank` is non-nullable and the element is MISSING from a document: canonical main (driver LINQ) throws `FormatException` (server `$add` of missing → null; the driver can't deserialize null into int). Native returns `0` for that row (observed: `[2, 3, 0]` for Rank 1/2/missing) — loud-on-main became silent wrong data, which the owner's goal forbids. Task 1.3 fixed the NULLABLE-operand case (`x.Score!.Value + 1`) via the ThrowsOnNull classification and deliberately did not flag non-nullable fields (doing so naively cascades declines over every `Select(x => x.A + x.B).Distinct()`/`.Sum()`).

**Design (g4-projection.md §T7 option (c)).** On the **Projection route only**, read every NON-NULLABLE COMPUTED projection leaf strictly: read the alias as `T?` and apply `.Value` (or an equivalent null check that throws `InvalidOperationException("Nullable object must have a value.")`), so a server null — from a missing non-nullable operand — throws instead of reading `default(T)`. This is a READ-side strictness change; it must NOT change emit-side classification (no new declines for Distinct/Sum/GroupBy over computed leaves — those operators don't read the computed leaf through the projection shaper, or if they do, measure it).
- Bare field leaves are excluded (Task 1.10 made a missing bare required scalar read `default(T)` on the Projection route, matching main — keep that; explicit BSON null on a bare leaf throws, also from 1.10).
- Computed leaves over NULLABLE operands are already ThrowsOnNull (Task 1.3) — keep them.
- Find where computed non-nullable leaves are read on the Projection route (MongoProjectionBindingRemovingExpressionVisitor alias reads → `BsonBinding.TryReadElementValue`), and add the strict read only for leaves classified Plain + computed (not bare field) + non-nullable CLR type. Prefer reusing the existing ThrowsOnNull read machinery with a new classification value or flag set at bind time by the same classifier (gate calls the fix's predicate).

**Measure first.** The plan says (c) "needs a full-suite measurement first": implement behind the narrowest condition, run the full suite ×3 AND an EF10 functional+spec run under `MONGODB_EF_QUERY_MODE=NativeOnly`, and report every test whose outcome changes (expected: none, other than the new tests). Any test that previously expected `0` for a computed leaf over a missing/null non-nullable operand must be checked against main (driver) — if main threw, the new throw is correct; if main returned a value, stop and report.

**Tests** (raw BsonDocument seeds: `{Title:"a",Rank:1}`, `{Title:"b",Rank:2}`, `{Title:"c"}`; 3 modes):
- Theory over shapes `x.Rank + 1`, `x.Rank * 2`, `Math.Abs(x.Rank)`, `x.Rank > 1 ? x.Rank : -1`, `new { V = x.Rank + 1 }`: NativeOnly and Native throw `InvalidOperationException` ("Nullable object must have a value" or the read's message — assert a stable fragment), DriverLinq throws `FormatException` (main parity — confirm the driver exception per shape; if the driver doesn't throw for some shape, assert its actual result and make native match).
- Well-formed rows only (`Where(x => x.Title != "c")`): all shapes return correct values in all modes (no regression).
- Bare `Select(x => x.Rank)` still reads `[1,2,0]` (Task 1.10 behavior unchanged).
- `Select(x => x.Rank + 1).Distinct()` and `.Sum()` over well-formed rows still go native (no new declines) — `NativeModeAssert.NativeAndParity`.
- Mutation check: disable the strict read → the theory fails.
- Full suite ×3.

**Commit:** `EF-322: throw for computed projections over missing non-nullable fields, matching driver LINQ`

### Task 1.13: F12 — closed local-collection filter folding bakes clock members into the cached plan

(Controller-authored task; added during Phase 1 execution from the Task 1.2 review.)

**Suspected problem.** `LocalCollectionFilterFoldingVisitor` evaluates `constantSource.Where(pred).ToArray()` at compile time when `pred` is closed; its `FreeVariableFinder` ignores clock members, so the first execution's reading is baked into the cached plan.

**Probe (EF10, `MONGODB_URI`/`ATLAS_URI` unset; one context, one cached compilation, 3.5 s wait; rows seeded at past and now+2 s).**

| Spelling | Branch (Native / NativeOnly / DriverLinq) | `upstream/main` `dec7e26f` |
|---|---|---|
| Captured array `candidates.Where(d => d < DateTime.UtcNow).Contains(r.Created)` (EF parameterizes the source; the fold takes the per-execution runtime-parameter path) | `[1]` then `[1,2]` in all three: correct, not reproducible | `[1]` then `[1,2]` |
| Constant source (inline literal array / `Expression.Constant(array)`, which EF leaves a `ConstantExpression`) | `[1]` then `[1]` in all three: **reproduced** (baked) | `[1]` then `[1,2]` |

The fold runs after EF's funcletizer, so only a literal/constant source folds at compile time. Because the preprocessor is mode independent, all three modes regressed against main. `Guid.NewGuid()` in the same predicate was also folded silently (main throws the EF-255 error); `Random.Shared.Next` is not affected (EF parameterizes `Random.Shared`, so the predicate is not closed).

**Fix.** (1) A predicate containing a non-deterministic call is never folded (`NonDeterministicCalls.ContainsNonDeterministicCall`, moved out of `MongoExpressionTranslator` so the translator and the visitor share one predicate). (2) A constant source whose predicate contains a clock (`RuntimeClock.ContainsClock`) takes the existing runtime-parameter path (extractor re-applies the filter on every execution) instead of the compile-time fold, so it stays native in NativeOnly (declining the fold instead left NativeOnly with a "not natively representable" gap, where main returned correct results).

**Tests** (`NonDeterministicFunctionTests`): constant-source clock fold per execution, captured-array clock fold per execution (pins the already-correct path), and `Guid.NewGuid` in a folded predicate not evaluated once; all in three modes. Mutation (revert the visitor): the constant-source and Guid tests fail in all three modes.

**Commit:** `EF-322: never fold local-collection filters that read the clock`

### Task 1.14: F13 — a computed projection whose server result is MISSING reads default on main but throws natively

(Controller-authored task; found by the Task 1.12 review.)

**Problem.** For a non-nullable computed leaf whose server expression evaluates to MISSING (not null), e.g. `Select(x => x.Rank > 1 ? -1 : x.Rank)` with `int Rank` missing from a document, canonical main returns `0` for that row: `$cond` selecting `"$Rank"` makes `$project` omit `_v`, and the driver's deserializer reads an omitted member as `default`. Natively the same query threw (Task 1.12's strict read). A blanket "missing → default" is wrong too: native renders `(long)x.Rank` as a bare `"$Rank"` (MISSING) where the driver renders `$toLong` (null, FormatException).

**Probe (Step 1).** Raw seeds a `{Rank:1, Score:5, Big:10}`, b `{Rank:2, Big:20}`, c `{}`, d `{Rank:null, Score:null, Big:null}`; `int Rank`, `int? Score`, `long Big`. Main = `upstream/main` `dec7e26f` exported with `git archive`, no `UseQueryMode`, EF10. Branch = EF-322c NativeOnly (Native identical in every row; DriverLinq identical to main in every row). Rows a/b agree everywhere. "throws" natively is `InvalidOperationException` (EF's "Nullable object must have a value." unless noted); main's "throws FE" is `FormatException` (`Cannot deserialize a 'Int32' from BsonType 'Null'`).

| Shape | c missing: main | c: before | c: after | d null: main | d: before | d: after |
|---|---|---|---|---|---|---|
| `x.Rank > 1 ? -1 : x.Rank` | 0 | throws | 0 | throws FE | throws | throws |
| `x.Rank > 1 ? x.Rank : -1` | -1 | -1 | -1 | -1 | -1 | -1 |
| `x.Rank > 1 ? x.Rank : x.Rank` | 0 | throws | 0 | throws FE | throws | throws |
| `x.Title == "c" ? x.Rank : 0` | 0 | throws | 0 | 0 | 0 | 0 |
| `x.Rank > 1 ? -1 : (x.Rank > 0 ? 5 : x.Rank)` | 0 | throws | 0 | throws FE | throws | throws |
| `x.Rank > 1 ? -1 : (int)x.Rank` | 0 | throws | 0 | throws FE | throws | throws |
| `x.Rank > 1 ? -1L : x.Big` | 0 | throws | 0 | throws FE | throws | throws |
| `x.Rank > 1 ? -1 : x.Rank + 1` | throws FE | throws | throws | throws FE | throws | throws |
| `x.Rank > 1 ? -1L : (long)x.Rank` | throws FE | throws | throws | throws FE | throws | throws |
| `(long)(x.Rank > 1 ? -1 : x.Rank)` | throws FE | throws | throws | throws FE | throws | throws |
| `x.Title == "c" ? x.Big : x.Rank (long)` | 0 | throws | throws | throws FE | throws | throws |
| `(int?)(x.Rank > 1 ? -1 : x.Rank)` | null | null | null | null | null | null |
| `x.Score ?? x.Rank` | 0 | throws (missing) | 0 | throws FE | 0 | throws |
| `x.Score ?? x.Rank + 1` | throws FE | 0 | throws | throws FE | 0 | throws |
| `(int)x.Rank` | 0 | throws (missing) | 0 | throws FE | 0 | throws |
| `(long)x.Rank` | throws FE | throws (missing) | throws | throws FE | 0 | throws |
| `(double)x.Rank` | throws FE | throws (missing) | throws | throws FE | 0 | throws |
| `(int?)x.Rank` | null | null | null | null | null | null |
| `x.Rank` | 0 | 0 | 0 | throws FE | throws | throws |
| `x.Rank + 0 (also - 0, 0 +, + captured 0)` | 0 | throws | throws | throws FE | throws | throws |
| `-x.Rank` | throws FE | throws | throws | throws FE | throws | throws |
| `Math.Abs(x.Rank)` | throws FE | throws | throws | throws FE | throws | throws |
| `new { V = x.Rank > 1 ? -1 : x.Rank }` | 0 | throws | 0 | throws FE | throws | throws |
| `new { V = (int)x.Rank }` | 0 | throws (missing) | 0 | throws FE | 0 | throws |
| `new { V = (long)x.Rank }` | throws FE | throws (missing) | throws | throws FE | 0 | throws |
| `condelse, then First()` | 0 | throws | 0 | throws FE | throws | throws |
| `condelse, then Where(v => v < 100)` | 0 | throws | 0 | throws FE | throws | throws |
| `condelse, then OrderBy(v => v)` | 0 | throws | 0 | throws FE | throws | throws |
| `condelse Concat condelse` | 0,0 | throws | 0,0 | throws FE | throws | throws |
| `condelse Union condelse` | 0 | throws | 0 | throws FE | throws | throws |
| `condelse, then Distinct()` | 0 | throws | throws | throws FE | throws | throws |
| `new { x.Title, V = condelse }, then Distinct()` | 0 | throws | throws | throws FE | throws | throws |
| `x.Score ?? x.Rank, then Distinct()` | 0 | 0 | throws | throws FE | 0 | throws |
| `castlongcond, then Distinct()` | throws FE | throws | throws | throws FE | throws | throws |
| `condelse, then Max()` | 0 | 0 | 0 | throws FE | 0 | 0 |
| `x.Rank, then Distinct()` | 0 | 0 | 0 | throws FE | 0 | 0 |
| `(int)x.Score (Score is int?)` | 0 | throws (missing) | throws (missing) | throws FE | 0 | 0 |
| `x.Score.Value` | 0 | throws | throws | throws FE | throws | throws |
| `x.Rank > 1 ? -1 : x.Score.Value` | 0 | throws | throws | throws FE | throws | throws |

**Discriminator (Step 2), from the table.** Native and driver render `$cond`/`$ifNull` identically, so the server's own answer is the oracle wherever the renderings agree: MISSING → `default`, null → throw. They disagree in three places: (1) native unwraps a widening cast (`TryTranslateValue`), so `(long)x.Rank` / `-1L : (long)x.Rank` / `(long)(cond)` are bare natively where the driver emits `$toLong`; (2) the driver simplifies `x + 0`, `x - 0`, `0 + x` and `x + <captured 0>` to `x`; (3) a projected `Distinct` groups natively on the value (`$group: {_id: {_v: …}}`, and a lone `$group` key turns MISSING into null) where the driver groups the projected document (`$$ROOT`, MISSING kept).

**Fix.**
- `ClassifyNonNullableValueRead` gains `DefaultOnMalformedMissing` (emit side: `Plain`): a leaf already `ThrowOnMalformedNull` whose value positions (`$cond` branches, `$ifNull` fallback) reach a stored field of exactly the read type (`MayAnswerMissing`). A reachable field of another type sits under a dropped widening (the driver's `$toX` answers null), which keeps the whole leaf strict. The missing-fields walk now also follows `$ifNull`'s fallback (`x.Score ?? x.Rank + 1` was read silently as `0` for c/d; main throws).
- Read side (`TryCreateThrowOnMalformedNullAliasRead`, same classifier call): `DefaultOnMalformedMissing` reads `default(T)` when the alias is absent, else `T?` + `.Value`.
- A projected Distinct's flattened key over such a leaf stays strict (`ThrowsOnMalformedNull`): (3) makes MISSING and null indistinguishable there.
- Bare cast leaves staged as the field (`TryCreateRequiredScalarCastRead`, Projection route, required non-nullable property): an identity cast (`(int)x.Rank`, dropped by the driver) reads like `x.Rank` (Task 1.10: missing → `0`, null → throws); a `$toX` cast (`(long)x.Rank`, `(double)x.Rank`) reads strictly, so an explicit null throws as on main instead of reading `0`.

**Residual divergences (loud; pinned in `Missing_required_element_divergences_from_main_are_loud`).** (2) additive-zero simplification (parameter-value dependent, which a compiled-once template can't follow); (1) a conditional mixing a same-typed field and a widened field (`c ? x.Big : x.Rank` as long) whose same-typed branch is MISSING; (3) Distinct over a field-selecting leaf for a document missing the field, including `(x.Score ?? x.Rank).Distinct()`, which read `0` (matching main for c, but also `0` for an explicit null) before this task. Out of scope, recorded: `Max` over a field-selecting leaf with an explicit null reads `0` (main throws); bare `x.Rank.Distinct()` and `(int)x.Rank` Distinct read an explicit null as `0` (main throws); casts over a nullable property (`(int)x.Score` missing → native throws, main `0`; null → native `0`, main throws) and `x.Score.Value` (Task 1.3's C# semantics: throws, main `0` for missing).

**Tests** (`NativeMissingRequiredScalarProjectionTests`): `Projection_over_a_missing_or_null_required_element_matches_main`, a theory over 32 shapes asserting main's per-row outcome (a/b/c/d) in NativeOnly, Native and DriverLinq; the Task 1.12 pin `Conditional_selecting_a_missing_required_element_throws_natively_and_reads_default_on_driver_linq` becomes `Conditional_selecting_a_missing_required_element_reads_default` (`[1, -1, 0]` in all modes); the residual pin above.

**Commit:** `EF-322: computed projections that evaluate to missing read default, matching driver LINQ`

---

## Phase 2 — Implicit-mode NativeOnly gaps (the 129 tests in `nativeonly-gaps.tsv`)

For every task: (1) run the listed existing tests with `MONGODB_EF_QUERY_MODE=NativeOnly` and confirm the predicted decline (if the decline site differs, add the temporary decline-reason capture from g6 §Task 0 — never committed — and update the design); (2) add the listed parity tests; (3) implement per the referenced design section; (4) run the bucket's tests in all three modes, mutation-check, full suite ×3; (5) commit `EF-322: native <bucket short name>`.

### 2.1 TPH / discriminators — design `g1-tph.md`

| Task | Change | Existing failing tests | New tests |
|---|---|---|---|
| 2.1.1 | `MongoDiscriminatorPredicate.TryBuild(entityType, includeDerived, negated, out predicate)` replaces `TryBuildDiscriminatorPredicate` (QMTEV:1966-1996); skips abstract types; serializes via the discriminator property (g1 Task 1) | — | `OfType_abstract_intermediate_does_not_match_documents_without_discriminator` |
| 2.1.2 | `TryTranslateHierarchyTypeTest` for `is` and `GetType() ==/!=` over a TPH root (`MongoExpressionTranslator.TypeIs.cs:44-45`, `.EntityType.cs:66-67`) (g1 Task 2) | `DiscriminatorTests`: `Can_configure_type_discriminator_element_name`, `…_with_int`, `Returns_correct_values_when_property_name_shared_between_entities`, `…navigation_shared…`, `Returns_correct_entity_where_is_type_query`, `…_GetType_query`, `Returns_correct_entities_with_mixed_query` | `NativeTphTypeTestTests.cs` (theory over real/shadow/int discriminators; code in g1) |
| 2.1.3 | `ResolveScopeEntityType` for members declared on a derived type after `OfType<T>()` (`MongoExpressionTranslator.Members.cs:127-130`) (g1 Task 3) | `OfType_does_not_break_entity_serializer_association` (3 modes) | OfType + derived member / sibling same CLR name different element name / OrderBy / Single; `((Customer)e).Name` → `DeclinesCleanly` |
| 2.1.4 | `LookupPipelineKind.FallbackOnly` → `DiscriminatorNarrowed`, admitted for collection Includes in `MongoSelectLowerer.AppendLookupStages` (`:459-505`); `LookupExpression` ctor uses `MongoEFDiscriminator.GetDiscriminatorsForTypeAndSubTypes` minus abstract (g1 Task 4) | `CrossCollectionRelationshipTests.Include_tph_derived_target_collection_nav_excludes_sibling_type_rows` | plain Include parity; filtered Include on derived target; converted discriminator |
| 2.1.5 | Test-only: `UnsupportedQueriesTests.Cast_to_child_not_supported_in_driver` pins `DriverLinq`; add NativeOnly refusal test; `GuidesDbContext.Create(…, MongoQueryMode? queryMode = null)` (g1 Task 6) | `Cast_to_child_not_supported_in_driver` | NativeOnly refusal |
| 2.1.6 | Docs: Query/AGENTS.md + stale headers in `TypeIs.cs`, `EntityType.cs` (g1 Task 7) | — | — |

Derived-`DbSet` root narrowing for `Join(db.Dogs)` (M2) is **out of scope** (wrong on main); record only.

### 2.2 Value converters / representation / read-back — design `g2-converters.md`

| Task | Change | Existing failing tests | Notes |
|---|---|---|---|
| 2.2.1 | `TryTranslateStoredAggregateField` gate at `NativeCardinalityBinder.cs:318` and `:300-306` (g2 T1 step 3) | `ProjectionTests.Sum_with_value_converter`, `Min_…`, `Max_…`, `Average_…`, `Sum_with_value_converter_via_ef_property` | Flips `RepresentedPropertyPushdownTests` `Big_*` pins; depends on 1.4 |
| 2.2.2 | Nested construction members over converted fields + `TryGetClientRelabelCastOverStoredField` (shared emit/read predicate) (g2 T2) | `Select_projection_nested_with_value_converter(_ef_property)`, `Select_projection_alias_with_bson_representation_widening_cast`, `…_nullable_lift` | negative: narrowing cast over string-stored long still declines |
| 2.2.3 | Dotted owned leaf with non-default property (g2 T3) | `OwnedEntity_projection_alias_with_bson_representation_uses_owned_property_serializer` | late-fallback leg required |
| 2.2.4 | `ToString()` over built-in to-string-converted property (g2 T4); double/float only if D-CULTURE accepted | `ValueConverterTests.Double_can_deserialize_and_query_from_string_default`, `Guid_can_deserialize_and_query_from_string_default` | negatives: default-stored Guid, `ToString("N")` |
| 2.2.5 | Static `Equals` nullability-only mismatch (g2 T5; same change as g7 Static_equals) | `FindTests.Find_equivalent_in_LINQ_v3_works_when_constant_nullable`; spec `Static_equals_nullable_datetime_compared_to_non_nullable` (EF8/9) | after 1.7 |
| 2.2.6 | `DateTimeOffset.TimeOfDay` leaf (g2 T6) | `DateTimeOffsetMemberProjectionTests.Select_DateTimeOffset_remaining_components` | — |
| 2.2.7 | Integral-narrowing converters in native comparisons (g2 T7; **D-EF337**) | `ProjectionTests.Count_with_value_converter_in_predicate_is_refused` → rename `Count_with_value_converter_in_predicate`, assert `== 4` in Native/NativeOnly, refusal under explicit DriverLinq | hazard test `Equality_against_out_of_int_range_constant_does_not_wrap` (3 modes; if DriverLinq fails it's M25) |
| 2.2.8 | Owned-collection element `Select` leaf (g2 T8 option B) | `OwnedEntity_collection_projection_alias_with_bson_representation_uses_owned_property_serializer`, `SharedClrTypeProjectionTests.*` (3) | resolve element type from the navigation, never CLR type |
| 2.2.9 | Test-only: `DateTimeOffsetMemberProjectionTests.*_throws` → `TestQueryMode.AssertRefusal<NotSupportedException>`; refresh stale EF-337 comments in `ProjectionTests.cs` (g2 T9) | `Select_DateTimeOffset_DateTime_component_with_string_representation_throws`, `…_with_value_converter_throws` | — |
| 2.2.10 | Restore the original `OrderBy(p => p.orderFromSun)` in the four `Select_projection_alias_with_bson_representation_*` tests **only if** D-EF337 extends to representation sorts; otherwise leave `OrderBy(name)` and record M16 | (mainworks rows 2–5) | default: leave + record |

### 2.3 Owned entities — design `g3-owned.md`

| Task | Change | Existing failing tests |
|---|---|---|
| 2.3.1 | Owned-collection `== null`/`!= null` (`TryResolveEntityTypedOperand` arm, `nullSafe: true`) (g3 Task A) | `OwnedEntity_collection_can_be_tested_for_null`, `…_not_null`, `OwnedEntity_collection_field_can_be_tested_for_null`, `…_not_null` |
| 2.3.2 | Owned-entity equality, per-property over non-shadow properties; `ElementParam`; `TryTranslateOwnedCollectionContains` (g3 Tasks B+C) | `OwnedEntity_nested_one_level_first_matching_location`, `…_where_no_matching_location`, `…_collection_match`, `…_collection_not_match`, `…_collection_any_match`, `…_collection_any_not_match` |
| 2.3.3 | Dotted scalar beside an array leaf (dotted alias + `CreateGetPropertyValueAtPath` read) (g3 Task D); flip pin `Ef362OwnedHopArrayProjectionTests.cs:189` | `OwnedEntity_dotted_scalar_leaf_projects_alongside_array_leaf`, `OwnedEntity_two_level_dotted_scalar_leaf_projects_alongside_array_leaf` |
| 2.3.4 | `ThenInclude` through an owned navigation (recursive `TryWalkIncludeChain`) (g3 Task F) | `ThenIncludeThroughOwnedNavigationTests.*` (2, EF10) |
| 2.3.5 | Nested owned collection inside an array-leaf element (spike first; g3 Task E); flip pin `NativeArrayProjectionTests.cs:360` | `OwnedEntity_collection_leaf_projection_with_nested_collection_element` |

New tests per g3 (`NativeOwnedCollectionNullCheckTests`, `NativeOwnedEntityEqualityTests` with `extra`/`reordered` rows to discriminate per-property vs whole-document equality).

### 2.4 Projections and `Mql.*` — design `g4-projection.md`

| Task | Change | Existing failing tests |
|---|---|---|
| 2.4.1 | `Mql.Field` resolves to a field in `TryResolveMember` (g4 T1) | `Select_projection_to_anonymous_via_mql_field`, `…_mixed_ef_property_and_mql_field`, `…_entity_and_mql_field`, `…_calculated_from_ef_property_and_mql_field`, `Sum_with_mql_field_and_field` |
| 2.4.2 | Flatten nested constructions (g4 T2) | `Select_projection_deeply_nested_anonymous`, `…_nested_anonymous_with_calculated_fields`, `…_nested_entity`, `…_nested_entity_and_scalar`, `…_complex_combination_nested`, `…_nested_anonymous_with_mql_field` |
| 2.4.3 | `Tuple.Create` normalization (g4 T3) | `Select_projection_to_tuple` |
| 2.4.4 | Renamed owned reference leaf (g4 T4); flip pin `NativeOwnedReferenceWholeEntityTests.Renamed_owned_reference_entity_leaf_falls_back_but_still_reads_correct_values` | `Select_projection_owned_type` |
| 2.4.5 | Primitive-collection count projection leaf (g4 T5; D-NULLSAFE) | `Select_projection_with_subquery_count` |
| 2.4.6 | `MongoFieldPresenceExpression` for `Mql.Exists/IsMissing/IsNullOrMissing` (g4 T6; update `MongoExpressionNodeCoverageTests`) | `MqlMethodTests.Where_Mql_Exists_does_not_return_entities_with_missing_property`, `…_missing_navigation`, `Where_Mql_IsMissing_returns_entities_with_missing_property`, `Where_Mql_IsMissingOrNull_returns_entities_with_null_or_missing_property` |

Harness: `GuidesDbContext.Create(…, MongoQueryMode? queryMode = null)`; `NativeProjectionParityTests` on `ReadOnlySampleGuidesFixture` running each `ProjectionTests` query through `NativeAndParity`.

### 2.5 Collections and dictionaries — design `g5-collections.md`

| Task | Change | Existing failing tests |
|---|---|---|
| 2.5.1 | Primitive arrays: `TryResolvePrimitiveCollectionPath`, `ArrayLength`/`.Length` in `TryMatchCountExpression` (g5 T1) | `WhereTests.Where_string_array_any`, `…_count`, `…_length`, `Where_string_list_any`, `…_count` |
| 2.5.2 | Composite-key `Contains` (`MongoCompositeKeyInExpression`, per-execution clause builder, 8th `PlaceholderTable` field) (g5 T3) | `CompositeKeyQueryTests.Should_match_entity_array_contains` |
| 2.5.3 | Dictionaries (`MongoDictionaryEntryExpression`, `MongoDictionaryContainsKeyExpression`, `MongoParameterExpression.ExplicitSerializer`; Step 0 unit assertion on `IBsonDictionarySerializer` + Document representation) (g5 T2) | `WhereDictionaryTests.*` (10), `UpdateEntityTests.Update_dictionary_property_with_struct_value` |

### 2.6 Joins and Includes — design `g6-joins.md`

| Task | Change | Existing failing tests |
|---|---|---|
| 2.6.1 | D6: no streaming for a bare join-inner entity leaf (`MongoShapedQueryCompilingExpressionVisitor.cs:651`) | `UnsupportedQueriesTests.Select_can_select_foreign_navigation` (add value assertions + seed with/without Company) |
| 2.6.2 | D3: `ScopeRerootingVisitor.VisitMethodCall` for `EF.Property` on a bare scope (`MongoTransparentScopeResolver.cs:80-110`) | `ShadowPropertyFlatJoinProjectionTests.*` |
| 2.6.3 | D2: bare scalar Select over a join chain (QMTEV:566-568) | `CrossCollectionIncludeTests.Chained_self_referencing_navigation_filter_resolves_reference_not_inverse_collection`, `Ef369MultiJoinComposedTests.ThenInclude_with_composed_projection_Select` (EF10) |
| 2.6.4 | D1: admit filter joins before a reference Include (`TryConfirmReferenceIncludeChain` :1480-1485; `TryAdmitFilterJoins`; admission shares `AreAllJoinsRowCountPreserving`) | `CrossCollectionRelationshipTests.Distinct_between_two_joins_returns_the_correct_rows`, `Skip_…`, `Take_…`, `Take_between_two_joins_to_same_target_entity_type_returns_the_correct_page` |
| 2.6.5 | D5: owned-navigation Include wrapper on a join leaf (`NativeJoinScopeProjectionBinder.cs:130`; `BindResultMember` QMTEV:3307/3328) | `UnsupportedQueriesTests.Join_can_be_translated` (assert values) |
| 2.6.6 | D4: nested construction around an inner entity (`AddEntityWrapperMember`) | `ShadowPropertyJoinProjectionTests.*` |
| 2.6.7 | D8 (EF8 only): `Count() > 0` comparison leaf beside `TryTranslateProjectedCollectionCount`; gate per version | spec `NorthwindNavigationsQueryMongoTest.Collection_select_nav_prop_predicate` (EF8) |
| 2.6.8 | D7: per **D-D7** | spec `NorthwindMiscellaneousQueryMongoTest.Perform_identity_resolution_reuses_same_instances_across_joins` |
| 2.6.9 | Test-only: `UnsupportedQueriesTests.GroupBy_cannot_be_translated`, `GroupBy_with_element_selector_cannot_be_translated` → `TestQueryMode.AssertRefusal<InvalidOperationException>` (main threw — not a parity case) | those 2 |

### 2.7 Spec scalar functions — design `g7-spec.md`

| Task | Change | Existing failing tests (EF versions) |
|---|---|---|
| 2.7.1 | `IsCompareCall` generalization (int/long/short/byte/sbyte/double/float/decimal/DateTime/TimeSpan `CompareTo` + `T.Compare`; relational fold only) | `Int_Compare_to_simple_zero`, `DateTime_Compare_to_simple_zero`, `TimeSpan_Compare_to_simple_zero` (8,9) |
| 2.7.2 | Central non-determinism scan in `VisitShapedQuery` (`NonDeterministicCalls.ThrowIfAnyNonDeterministic` incl. `EF.Functions.Random`), all modes (**D-RAND**); delete `IsNativeOnly` blocks in `Random_next_is_not_funcletized_1..6`; `Random_return_*` expect `InvalidOperationException` containing "Random" | `Random_next_is_not_funcletized_1..6`, `Random_return_less_than_1`, `Random_return_greater_than_0`, `Where_guid_newguid` (8,9) |
| 2.7.3 | Split `String_Compare_nested` / `String_Compare_to_nested` overrides per sub-query (**D-TOUPPER**) | those 2 (8,9) |
| 2.7.4 | `CurrentCulture[IgnoreCase]` → ordinal in `TryMatchRegexMethod` (**D-CULTURE**) | `String_StartsWith/EndsWith/Contains_with_StringComparison_unsupported` (9) |
| 2.7.5 | Date part over `AddYears` receiver; `AddTicks` via shared `TryGetDateAddShape` (constant multiple of 10 000 only) | `Where_date_add_year_constant_component` (8,9), `Select_expression_datetime_add_ticks` (8,9,10; remove its `IsNativeOnly` branch) |
| 2.7.6 | `DateTime.ToString()` client evaluation (**D-TOSTRING**) | `Select_expression_other_to_string` (8,9,10; add `AssertMql`) |
| 2.7.7 | Bitwise `& | ^ ~` on int/long (`MongoBinaryOperator.BitAnd/BitOr/BitXor`, `MongoUnaryOperator.BitNot`, new `MapBitwiseOperator`; gate the `and` override on `TestServer.SupportsBitwiseOperators` or special-case `$bitsAllSet`) | `Where_bitwise_binary_and/or/not` (8,9), `…_xor` (9) |
| 2.7.8 | `g.Key` accumulator operand via `TryResolveKeyReferenceAsRawExpression` in `TryBindAccumulator` (NativeGroupByBinder.cs:810-815); remove `IsNativeOnly` branch | `GroupBy_aggregate_using_grouping_key_Pushdown` (8,9,10) |
| 2.7.9 | AdHocJson: Task 0 decline capture for rows 1–2; row 2 (`Project_top_level_entity_with_null_value_required_scalars`, main-correct) made native; rows 1, 3, 4 (main threw) pinned with `AssertNativeTranslationFailedAsync` under NativeOnly | `AdHocJsonQueryMongoTest.*` (9) |

---

## Phase 3 — Explicit decline pins whose driver oracle is correct

Only families confirmed WORKED on canonical `main` by Task 0.4 are in scope. For each in-scope pin: replace `DeclinesCleanly(...)` / `Throws<NativeTranslationNotSupportedException>` with `NativeAndParity(...)` (or `NativeAndExpected` + raw seeds), implement per the family approach (pin rows with decline sites and sketches in `pins1/2/3.tsv`), mutation-check, full suite ×3. Families already covered by Phase 2 tasks are listed with that task.

Abbreviations — test files (under `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`, `Tests.cs` dropped): GB `NativeGroupBy`, DI `NativeDistinct`, GJ `NativeGroupByOverJoin`, SO `NativeSetOps`, NAP `NativeArrayProjection`, NCT `NativeCast`, NCDT `NativeConditionalAndDateTime`, NDCP `NativeDocumentConstructionProjection`, NCEL `NativeClientEvaluatedLeaf`, NSOP `NativeSetOperationProjection`, NJT `NativeJoin`, NCJP `NativeChainedJoinPaging`, NOCP `NativeOwnedCollectionPredicate`, NVST `NativeVectorSearch`; others are spelled out (`OwnedCount` = `NativeOwnedCollectionCount`, `OwnedAll` = `NativeOwnedCollectionAll`, `FilteredCount` = `NativeOwnedCollectionFilteredCount`, `CompBare` = `NativeComputedBareProjection`, `ProjReducer` = `NativeProjectionReducer`, `OwnedRef` = `NativeOwnedReferenceWholeEntity`, `CtorOnly` = `NativeCtorOnlyProjection`, `BareProj` = `NativeBareProjection`, `RefCount` = `NativeReferenceCollectionCountPredicate`, `JoinCond` = `NativeJoinScopeConditionalProjection`, `JoinNested` = `NativeJoinScopeNestedProjection`, `Chained` = `NativeChainedReferenceNavigationFilter`, `Sort` = `NativeComputedSort`, `Bool` = `NativeBoolBitwise`, `LocalColl` = `NativeLocalCollectionContains`, `ClientMethod` = `NativeClientMethodProjection`, `GroupByCtor` = `NativeGroupByCtorProjection`, `ContainsTerminal` = `NativeContainsTerminal`, `CompProj` = `NativeComputedProjection`, `Cardinality` = `NativeCardinality`, `DTO` = `DateTimeOffsetMemberProjection`). Source (under `src/MongoDB.EntityFrameworkCore/Query/`): QMTEV `Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, MSQCEV `Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`, NSP `NativeTranslation/NativeSlotPopulator.cs`, NGB `NativeTranslation/NativeGroupByBinder.cs`, NCB `NativeTranslation/NativeCardinalityBinder.cs`, NPB `NativeTranslation/NativeProjectionBinder.cs`, NJSPB `NativeTranslation/NativeJoinScopeProjectionBinder.cs`, MET `NativeTranslation/MongoExpressionTranslator.cs` (+ partials). Line numbers are from `c7854e7f`; re-locate by symbol if they drift.

| Family | Pins (file:line) | Decline site | Approach | Covered by |
|---|---|---|---|---|
| A post-group OrderBy/Skip/Take | GB:2502, GB:3333, GJ:583 | `NativeSlotPopulator.cs:139-142` (+:161 joined) | widen post-group ops to sort/paging under `IsFinalizedKeyedGroupOutput`; keys via `MongoProjectedAliasScope` | new 3.1 |
| B ops after nested GroupBy | GB:3366, GB:3385 | NSP:137, NCB:272-274 (`MongoSelectDefinition.cs:749-751`) | op list after the outer `$group` flatten | 3.2 |
| C >2 grouping levels / paging between levels | GB:2145, GB:3917 ×3 | QMTEV:2170-2179 | stack of grouping stages (refactor) | 3.3 |
| D group paging before terminal aggregate/HAVING | GB:1275, GB:1309 | NGB:1472-1473, NSP:60-63 | single ordered group-op list | 3.4 |
| E post-group Where alias shapes | GB:3435, GB:3469 ×2, GB:3748 | MET.Members:248-279 | Guid key via key-part `IProperty`; nested alias fold/resolve; `$size` over list alias | 3.5 |
| F GetType over a non-entity row | GB:3508 ×2, GB:3526 ×2, GB:3543 | MET.EntityType:81 | fold for sealed non-entity row types | 3.6 |
| G Distinct over grouped output | DI:200 | NGB:1777 | snapshot prior grouping + key-only `$group` | 3.7 |
| H one-member wrapper structural equality | DI:217, 235, 250 | MET:772-780, Members:70-72/191-193 | decompose anonymous/ValueTuple equality per key part (mutation-proven; previously 0 rows) | 3.8 |
| I group/Distinct key with non-default serialization | GB:268, DI:732, 744, 1260 | MET:173 via NGB:142; NGB:1788-1790/1840-1841 | stored-form grouping is equality-faithful; read `_id` through property serializer | 3.9 |
| J `$push` over non-default element | GB:3789 ×2, GB:3798 | NGB:890-898 | list serializer wrapping the property serializer (Local kind tested) | 3.10 |
| K group accumulator/key gaps | GB:465, 718, 1334, 1438, 1821, 1858, 2447, 2561 | various NGB/NCB (pins1) | eight independent small changes (pins1 family K) | 3.11 (one commit per shape) |
| L ops after projected-operand set op | SO:1636, 1662, 1688, 1692, 1728, 1753, 1780, 1811, 1838, 2339 | NSP:139-142, NCB:265, QMTEV:2164-2167/3460/3579, NGB:1778 | `IsProjectedSetOpOutput`; alias scope over source1 projection; TrailingOps | 3.12 |
| M grouped set-op operand with paging/HAVING/order | SO:1213, 1241, 1268 | QMTEV:3644-3646 | operand lowerer emits HAVING/sort/paging | 3.13 |
| N ops after whole-entity set op | SO:765, SO:1062 | QMTEV:1480, 1938 | OfType conjunct in TrailingOps; deferred reference lookup | 3.14 |
| O join-scope set-op operands | GJ:1237, 1246, 1281 | QMTEV:3462/3606/3657/3686-3693 | constant-leaf rebind; two-level chain operands | 3.15 |
| P case-mapping comparison over computed receiver | DI:1176, 1386 | MET.CaseMapping:46-47 | `$regexMatch` with `i` over computed receiver (never `$toUpper`) | 3.16 |
| Q Join over projected Distinct | DI:2004 | QMTEV:2247-2250 | emit Distinct `$group` before `$lookup` | 3.17 |
| R paging before grouped join | GJ:625, 641 | QMTEV:1097-1103 | keep pre-join paging in PipelineOps | 3.18 |
| owned-quantifier / quantifier aggregates `$expr` | NCT:371, NOCP:423, OwnedCount:849/864, OwnedAll:430/441, FilteredCount:420, Cardinality:400 | MET.cs:1128/1144-1145, Negator:104, NCB:348 | `MongoQuantifierExpression` (`$anyElementTrue`/`$allElementsTrue` over `$map`) as top-level `$expr`; never inside `$elemMatch` | 3.19 |
| primitive collections | OwnedCount:833, OwnedAll:512, FilteredCount:405 | Members.cs:544-546 | sibling resolver with scalar element scope | 2.5.1 + 3.20 |
| array-leaf set-op operand | NAP:871, 910, 938, 1001 | QMTEV:3573 | exempt Concat; Union dedup on projected fields minus owner `_id` | 3.21 |
| array-leaf computed sibling | NAP:593, 620, 649, 1228 | NPB:476-478 | computed sibling staged as `NativeComputedLeafExpression`; mixed reader evaluates on late fallback | 3.22 |
| array-leaf alias / nav-leaf alias | NAP:287, 531; OwnedRef:813, 856; CtorOnly:186; BareProj:399; Ef362:185, 213 | NPB:1483, 1089-1095, 470-479, 1836 | document-path alias override; late-fallback agreement | 2.4.4 + 3.23 |
| array-leaf nested owned element | NAP:382 | NPB:1486 | as 2.3.5 | 2.3.5 |
| list-init container | NAP:1814 | ExpressionExtensionMethods.cs:142 | ListInit container (`_ctorArg<N>`) + fallback strip | 3.24 |
| TimeOfDay downstream / predicate / shape | NCDT:303, 317, 376, 409, 395, 594 (425, 452 conditional) | NGB:1794/142, NCB:305, NSP:614, MET:261/291 | TimeOfDay (ms) flag on key parts/aggregate operand/sort key; `$expr` ms comparison | 3.25 (only if 0.4 confirms main worked — EF-218 rewrite not on main) |
| TOD set-op Concat | NCDT:360 | QMTEV:3780/3727 | operand tag + per-operand serializer | 3.26 (conditional) |
| doc construction computed / client ternary / class-map ternary | NDCP:257, 478, 552, 647, 675, 723 (+750/769/789/816 if confirmed) | NPB:942-949, 626, 441-443; NJSPB:292; QMTEV:575; NGB:324/586 | computed members in `MongoDocumentConstructionExpression`; branch-qualified staged reads; class-map element names | 3.27 |
| client-evaluated leaves downstream / set ops / row-independent members | NCEL:343, 365, 388, 414, 435, 477, 492, 509, 524 | QMTEV:304/3578/3619, MET:1177-1185, NSP:614 | per-execution derived parameters; drop row-independent sort keys; operand tags | 3.28 |
| set-op constant / construction / realias | NSOP:213, 462, 469, 476, 485, 493, 512, 544, 571, 584, 592, 600 | QMTEV:3657-3720 | shaper rebind to flattened alias; per-member rebind; round-trip-safe constant types (Guid via F9 root) | 3.29 |
| chained-join paging / reducers / both-side paging / interleaved paging | NCJP:69, 83, 98, 111; NJT:491, 715, 721; Ef373:197 | QMTEV:1017/1028/2287-2289, NCB:57, `MongoSelectDefinition.cs:1074` | record ops with their join level; lowerer emits per level (high-traffic guard → full suite) | 3.30 |
| join misc | NJT:827[Split], 1797, 2060 ×2; JoinCond:348, 371; JoinNested:150, 165, 180; Chained:122 | see pins2/pins3 | `$split` arm; `$$ROOT` + computed; nested include levels; null-check projections; flat-alias nested members; cross-hop `$expr` | 3.31 |
| bare computed over `$size` | CompBare:535, 641, 957, 1001, 1209; ProjReducer:196 | NPB:1906-1960/2020/2046 | admit null-safe size nodes; strip Synthetic aliases on every late-fallback route | 3.32 |
| ref-count composition | RefCount:146, 189, 234, 267 | `MongoSelectDefinition.cs:1245-1246` | lowerer emits count `$lookup`+`$match`+`$unset` before set op / `$group` / join | 3.33 |
| DateTimeOffset ToString client | DTO:266, 268, 271, 338 | MSQCEV:321, MET:1944 | stage receiver, re-apply on read (same mechanism as 2.7.6) | 3.34 |
| owned-leaf set-op / Distinct | OwnedRef:934, 947; BareProj:481 | QMTEV:3573, NGB:1765 | no owner `_id` in operands; owned materializer no-owner-key mode (NoTracking) | 3.35 |
| small singles | Sort:465, 562 (row-independent keys); Ef382:360, 408 (array Contains in `$expr`); Bool:161, 197 (nullable bool bitwise `== true`); LocalColl:161, 207; ClientMethod:137; OfType:185 (→2.1.3); GroupByCtor:205; ContainsTerminal:121; CompProj:470 (Split); vector NVST:299, 327, 518; Ef425:209 (`$map` leaf) | pins2/pins3 | per pins sketches | 3.36 (one commit per item) |

Out of scope (driver threw, wrong, no oracle, or owner-ruled): every pin classified THROWS / WRONG / NO-ORACLE / MECHANISM in `pins*.tsv`, plus D-PIN-OWNERRULED. WRONG ones are already in `main-issues.md` (M14–M18, M26–M28 etc.). **UNSURE** pins (8 + 10 + 7): classify via Task 0.4 probes before deciding.

When Split goes native (3.31/3.36), replace the "unsupported shape" in `QueryModeGateTests.cs:164` and `NativeGateRoutingTests` with another genuinely unsupported shape (e.g. `PadLeft`).

---

## Phase 4 — Remove divergence and gate

### Task 4.1: Remove `IsNativeOnly` success/failure divergence in spec overrides

- [ ] **Step 1:** List every `MongoSpecTestHelpers.IsNativeOnly` branch (≈146 methods, `grep -rn IsNativeOnly tests/MongoDB.EntityFrameworkCore.SpecificationTests`). Classify each: **MQL-only** (both branches assert the same outcome, only `AssertMql` differs — keep), or **outcome-divergent** (one branch asserts success, the other a failure).
- [ ] **Step 2:** For each outcome-divergent branch: the success expectation must now hold in NativeOnly (thanks to Phases 1–3) → collapse to the single success branch; if NativeOnly still fails, the test is a known gap — either fix it (add to the owning Phase task) or, if main threw, collapse to the failure expectation.
- [ ] **Step 3:** Run the spec suite with `MONGODB_EF_NATIVE_ONLY=1` and without, EF8/EF9/EF10 — both green.
- [ ] **Step 4: Commit** — `EF-322: remove native-only outcome divergence from spec overrides`

### Task 4.2: Final gates

- [ ] **Step 1:** Build all three configurations; run the full default suites (`/test-all`) → 0 failures.
- [ ] **Step 2:** Run all suites with `MONGODB_EF_NATIVE_ONLY=1` for EF8, EF9, EF10 → 0 failures (functional suites honor it since Task 0.1).
- [ ] **Step 3:** Run `tests/tools/native-parity-diff.sh` for EF8/EF9/EF10 → the regress list is empty, except tests whose DriverLinq pass is a known-wrong result (each listed with its `main-issues.md` ID in the commit body).
- [ ] **Step 4:** Re-run the review probes (`ReviewProbeTests.cs.txt`, added as permanent tests across Phase 1) → every F-row matches main or the decision table.
- [ ] **Step 5:** Update `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (new invariants from g1–g7), `BREAKING-CHANGES.md` (EF-217 entry; D-F10; any refusal changes), and mark Phase 3 families done in `pins-main-confirmation.md`.
- [ ] **Step 6:** Squash per the finishing-a-native-slice convention (`-presquash` backup), ff onto `EF-322c`. Push only when the owner asks.

---

## Phase 5 — Track issues for `main`

### Task 5.1: Maintain `main-issues.md`

- [ ] As Task 0.4 and Phase 3 probes run, add/upgrade rows in `docs/superpowers/findings/2026-10-01-native-parity/main-issues.md` (status: confirmed / test comment / code reading).
- [ ] For each **confirmed** row, draft an EF Jira ticket (title `<symptom>`, repro LINQ + seed, main result vs correct, native-branch status and fixing commit if any). Show drafts to the owner before filing (Jira MCP: `jira_create_issue`, then `jira_update_issue` for Markdown→wiki formatting; fence code).
- [ ] Commit the doc updates with the slice.

---

## Execution order and checkpoints

1. Phase 0 (0.1 → 0.2 → 0.3 → 0.4). Checkpoint: differential runner reproduces `nativeonly-gaps.tsv`; Phase 3 scope confirmed.
2. Phase 1 (1.1, 1.7 first — they unblock 2.2.5; then 1.2–1.6, 1.8, 1.10, 1.11). Checkpoint: full suite ×3 + review probes.
3. Phase 2 buckets in this order (smallest blast radius first): 2.7 → 2.2 → 2.1 → 2.5 → 2.4 → 2.3 → 2.6. Checkpoint after each bucket: full suite ×3 and `MONGODB_EF_QUERY_MODE=NativeOnly` functional run.
4. Phase 3 families, largest in-scope count first (L, K, quantifiers, bare-size, alias families, …), each its own slice.
5. Phase 4, then Phase 5 continuously.
