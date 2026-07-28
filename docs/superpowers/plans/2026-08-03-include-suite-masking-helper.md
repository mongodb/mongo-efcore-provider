# Make the Include specification suites fail on wrong data — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Replace the bare-`catch` `AssertTranslationFailed` shadow in the four `Northwind*Include*` specification
suites with the strict shared helper, so those suites fail on wrong data instead of silently accepting it.

**Architecture:** Test-infrastructure change only — nothing in `src/`. Each of the four suites keeps its local
shadow (so all 234 call sites are untouched) but the body becomes a one-line delegation to the existing
`MongoSpecTestHelpers.AssertNativeTranslationFailedAsync`. The 8 test cases that the strict helper correctly
rejects are the already-documented EF-X010, and they get baselined honestly with
`Assert.ThrowsAnyAsync<XunitException>` rather than skipped.

**Tech Stack:** xUnit, EF Core specification-test suites (`Microsoft.EntityFrameworkCore.Specification.Tests`),
MongoDB C# driver 3.10.0, TestContainers (`mongodb/mongodb-atlas-local`).

**Design spec:** `docs/superpowers/specs/2026-08-03-include-suite-masking-helper-design.md`. Read §2 before
starting — the Task-0 spike contradicted the premise this work was scheduled on, and the plan below assumes you
know that.

## Global Constraints

- **JIRA prefix:** every commit message starts with `EF-367: `.
- **Branch:** `EF-367`, based on `EF-366` @ `44dfe5a`. Do not rebase or squash it; the EF-366 merge decision is
  still open and independent of this work.
- **Preserve file BOMs.** All four test files and `docs/failing-spec-tests.md` must keep their existing encoding.
- **No `[Skip]` / `[ConditionalTheory(Skip=...)]` anywhere in this work.** The project convention is to baseline
  failures honestly so they flip loudly when behaviour changes. This is a hard constraint, not a preference.
- **Three EF versions matter.** 130 of the 234 call sites are `#if EF8 || EF9` and 4 are `#if !EF8 && !EF9`, so an
  EF10-only run compiles just 104 of 234. Any "it's green" claim must cover EF8, EF9 **and** EF10.
- **Compare test results by test NAME parsed from raw TRX, never by count.** Counts differ legitimately between
  EF versions (952 on EF10 vs 944 on EF8/EF9) and a count match can hide an offsetting PASS→FAIL.
- **Preserve each file's existing modifier order** on the shadow declaration: three suites use
  `protected new static`, `NorthwindStringIncludeQueryMongoTest.cs` uses `protected static new`. Leave each as-is
  so the diff is confined to the method body.
- **Keep the `async`/`await` form** of the shadow (see Task 1). This is deliberate: it is the exact signature the
  spike compiled and ran green on all three EF versions. Do **not** "tidy" it to the expression-bodied
  `Task`-returning form, even though the sibling suite at `NorthwindGroupByQueryMongoTest.cs:2572` uses that form.
- **Run tests with `MONGODB_URI` and `ATLAS_URI` unset** so TestContainers boots an isolated `atlas-local`
  container per test process. Container boot takes a while; use generous timeouts and background long runs.

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs` | Modify 2 regions | Shadow body (`:1317-1329`) + EF-X010 override (`:1306-1314`) |
| `tests/.../Query/NorthwindIncludeNoTrackingQueryMongoTest.cs` | Modify 2 regions | Shadow body (`:1201-1213`) + EF-X010 override (`:1190-1198`) |
| `tests/.../Query/NorthwindStringIncludeQueryMongoTest.cs` | Modify 2 regions | Shadow body (`:1324-1336`) + EF-X010 override (`:1314-1322`) |
| `tests/.../Query/NorthwindEFPropertyIncludeQueryMongoTest.cs` | Modify 2 regions | Shadow body (`:1323-1335`) + EF-X010 override (`:1313-1321`) |
| `docs/failing-spec-tests.md` | Modify 2 sections | EF-149 row (`:28`), EF-X010 section (`:143-145`) |
| `docs/superpowers/specs/2026-08-03-include-suite-masking-helper-design.md` | Read only | The spec. Do not edit. |

No files are created. `MongoSpecTestHelpers` is **not** modified — its accept-list needs no widening, and that is
measured (spec §2.3), not assumed.

Useful facts, already verified, so you do not need to re-check them:

- All four suites already have `using Xunit.Sdk;` at line 18, so `XunitException` resolves unqualified.
- `MongoSpecTestHelpers` is in namespace `MongoDB.EntityFrameworkCore.SpecificationTests.Query` — the same
  namespace as all four suites — so no `using` needs adding.
- The four `Include_collection_with_client_filter` overrides are currently **byte-identical** to each other.
- The four shadow bodies are byte-identical to each other apart from the `new static` / `static new` ordering.

---

### Task 1: Make the shadow strict and baseline the EF-X010 reds

**Files:**
- Modify: `tests/.../Query/NorthwindIncludeQueryMongoTest.cs:1306-1314` and `:1317-1329`
- Modify: `tests/.../Query/NorthwindIncludeNoTrackingQueryMongoTest.cs:1190-1198` and `:1201-1213`
- Modify: `tests/.../Query/NorthwindStringIncludeQueryMongoTest.cs:1314-1322` and `:1324-1336`
- Modify: `tests/.../Query/NorthwindEFPropertyIncludeQueryMongoTest.cs:1313-1321` and `:1323-1335`

**Interfaces:**
- Consumes: `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(Func<Task> query, params Type[] additionalAcceptedTypes)`
  — `internal static async Task`, defined at
  `tests/.../Query/MongoSpecTestHelpers.cs:46-67`. Called here with **no** additional accepted types.
- Produces: nothing consumed by later tasks. Tasks 2 and 3 depend only on this task's committed state.

**Why both edits are in one task:** committing the strict helper without the EF-X010 baseline would leave the tree
red at that commit and break bisect. The intermediate red is still *observed* (Step 3) as the evidence that the
helper now detects failures — it just is not committed on its own.

- [ ] **Step 1: Confirm your starting point**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git branch --show-current      # expect: EF-367
git status --short             # expect: clean
git log --oneline -1           # expect: the spec-pinning commit 235107b or later
```

- [ ] **Step 2: Make all four shadows strict**

In each of the four files, replace the shadow body. The declaration line is unchanged — **including its
modifier order**, which differs in the String suite. Before, in `NorthwindIncludeQueryMongoTest.cs`,
`NorthwindIncludeNoTrackingQueryMongoTest.cs` and `NorthwindEFPropertyIncludeQueryMongoTest.cs`:

```csharp
    protected new static async Task AssertTranslationFailed(Func<Task> query)
    {
        try
        {
            await query();
        }
        catch
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected query to fail but it succeeded.");
    }
```

After:

```csharp
    protected new static async Task AssertTranslationFailed(Func<Task> query)
        => await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(query);
```

In `NorthwindStringIncludeQueryMongoTest.cs` the same body replacement applies, but keep its declaration
exactly as it is:

```csharp
    protected static new async Task AssertTranslationFailed(Func<Task> query)
        => await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(query);
```

- [ ] **Step 3: Build and run the four suites on EF10 — expect exactly 8 failures**

This is the failing-test observation that proves the helper now detects what it previously swallowed.

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
mkdir -p "$SP/ef367"
FILTER='FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest'

dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "$FILTER" \
  --logger "trx;LogFileName=$SP/ef367/task1-strict-ef10.trx"
```

Expected: build succeeds; **952 total, 944 passed, 8 failed**. The 8 are
`Include_collection_with_client_filter(async: False)` and `(async: True)` in each of the four suites. Any other
failure means something is wrong — stop and report rather than proceeding.

Extract the failing names by name, not by reading the console tail:

```bash
python3 - "$SP/ef367/task1-strict-ef10.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
root = ET.parse(sys.argv[1]).getroot()
c = root.find(f'{NS}ResultSummary/{NS}Counters')
print('total=%s passed=%s failed=%s' % (c.get('total'), c.get('passed'), c.get('failed')))
for u in root.iter(f'{NS}UnitTestResult'):
    if u.get('outcome') == 'Failed':
        print('FAIL', u.get('testName'))
PY
```

- [ ] **Step 4: Baseline the four EF-X010 overrides**

All four `Include_collection_with_client_filter` overrides are currently byte-identical. In each file, replace:

```csharp
    public override async Task Include_collection_with_client_filter(bool async)
    {
        // Fails: Throws with Mongo-specific message rather than the generic EF message. EF-X010
        await AssertTranslationFailed(() => base.Include_collection_with_client_filter(async));
        AssertMql(
            """
Customers.
""");
    }
```

with:

```csharp
    public override async Task Include_collection_with_client_filter(bool async)
    {
        // Fails: Throws with Mongo-specific message rather than the generic EF message. EF-X010
        // The base test does its own Assert.ThrowsAsync<InvalidOperationException> and Assert.Contains on EF's
        // message; the provider throws the driver's ExpressionNotSupportedException, so what escapes base is an
        // Xunit.Sdk.ThrowsException. That is a wrong exception *type*, not a translation failure, so the strict
        // AssertTranslationFailed helper correctly rejects it and must not be used here. Baselined per the
        // project convention: when the provider starts throwing EF's type, the base assertion passes, this
        // ThrowsAnyAsync fails, and the test demands a real re-baseline.
        await Assert.ThrowsAnyAsync<XunitException>(() => base.Include_collection_with_client_filter(async));
        AssertMql(
            """
Customers.
""");
    }
```

Keep the `AssertMql("Customers.")` baseline exactly as-is — it is a real, deliberate non-empty baseline (this
query does emit a `$match` on `Customers` before failing), and it is one of only 8 call sites without the
incidental empty-`AssertMql()` net.

- [ ] **Step 5: Re-run the four suites on EF10 — expect zero failures**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
FILTER='FullyQualifiedName~NorthwindIncludeQueryMongoTest|FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest|FullyQualifiedName~NorthwindStringIncludeQueryMongoTest|FullyQualifiedName~NorthwindEFPropertyIncludeQueryMongoTest'

dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "$FILTER" \
  --logger "trx;LogFileName=$SP/ef367/task1-green-ef10.trx"
```

Expected: **952 total, 952 passed, 0 failed, 0 skipped.** Verify with the same Python snippet from Step 3.

- [ ] **Step 6: Confirm no `Skip` was introduced**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git diff -U0 | grep -n "Skip" || echo "OK: no Skip in the diff"
```

Expected: `OK: no Skip in the diff`.

- [ ] **Step 7: Commit**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindEFPropertyIncludeQueryMongoTest.cs
git commit -m "EF-367: make the four Include spec suites fail on wrong data

The local AssertTranslationFailed shadow caught every exception bare, so it
could not distinguish a translation failure from the base test's result
mismatch. Delegate it to the strict shared helper instead, leaving all 234
call sites untouched.

That correctly rejects the 8 EF-X010 cases, whose failure is a wrong exception
TYPE rather than a translation failure: the base test does its own
Assert.ThrowsAsync<InvalidOperationException> and the provider throws the
driver's ExpressionNotSupportedException, so an Xunit ThrowsException escapes.
Baselined with Assert.ThrowsAnyAsync<XunitException> per the project
convention, so the test flips loudly if the provider ever starts throwing EF's
type. No Skip added.

Measured, not assumed: this masks zero wrong-data failures today, because all
234 sites fail to translate outright. It is insurance for the reference-Include
slice, which will legitimately start emitting MQL here and dissolve the
incidental empty-AssertMql() net that is currently doing the real work."
```

---

### Task 2: Correct the failing-spec-tests documentation

**Files:**
- Modify: `docs/failing-spec-tests.md:28` (the EF-149 table row) and `:143-145` (the EF-X010 section)

**Interfaces:**
- Consumes: nothing. Documentation only.
- Produces: nothing.

Two corrections, both grounded in the spike measurement (spec §2.3) rather than in prose.

- [ ] **Step 1: Record the `VisitChildren` finding on the EF-149 row**

`docs/failing-spec-tests.md:28` is a single long table row for EF-149. Append the following to the end of its
**Description** cell (the third `|`-delimited column), before the closing `|` and the count. Do not alter the
existing text, and keep it on one line — the row is a Markdown table row and must not gain a newline:

```
 Measured 2026-08-03 (EF-367): within the four Northwind Include suites, 24 of these tagged call sites — the 6 `*GroupBy_Select` method names (`Include_collection_GroupBy_Select`, `Include_collection_Join_GroupBy_Select`, `Include_reference_GroupBy_Select`, `Include_reference_Join_GroupBy_Select`, `Join_Include_collection_GroupBy_Select`, `Join_Include_reference_GroupBy_Select`), each overridden in all four suites — do not fail with EF's normal "could not be translated" message but with a leaked internal EF guard, `InvalidOperationException: Calling 'ShapedQueryExpression.VisitChildren' is not allowed`. Still a genuine translation failure, not wrong data, so it stays tagged EF-149; recorded here because the message is scruffy and user-visible.
```

- [ ] **Step 2: Correct the stale EF-X010 section**

`docs/failing-spec-tests.md:143-145` currently describes the EF-X010 tests inaccurately in two ways: it claims
they "use `Assert.ThrowsAsync<ContainsException>`" (they did not — they used the local bare-catch
`AssertTranslationFailed`), and it claims the override "carries an explanatory comment … but no `// Fails:` tag in
the current codebase" (all four do carry the tag). Replace the two prose lines under the heading:

```markdown
### EF-X010 — Provider-specific Include error message differs from EF baseline
Pattern: `Include_collection_with_client_filter` across all four Include variants. The upstream base test asserts
`Assert.Contains(<EF message>, (await Assert.ThrowsAsync<InvalidOperationException>(...)).Message)`; the provider
instead throws the driver's `ExpressionNotSupportedException`, so an `Xunit.Sdk.ThrowsException` escapes the base
method. This is a wrong exception *type*, not a translation failure, so the strict
`MongoSpecTestHelpers.AssertNativeTranslationFailedAsync` correctly rejects it. As of EF-367 each override carries
the `// Fails: … EF-X010` tag and baselines the current behaviour with
`Assert.ThrowsAnyAsync<XunitException>(() => base.Include_collection_with_client_filter(async))`, so the test
flips loudly if the provider ever starts throwing EF's exception type. Each also keeps a real non-empty
`AssertMql("Customers.")` baseline, because the query does emit a `$match` on `Customers` before failing.
Affected: 4 tests / 8 cases (`NorthwindEFPropertyIncludeQueryMongoTest.cs`, `NorthwindIncludeNoTrackingQueryMongoTest.cs`, `NorthwindIncludeQueryMongoTest.cs`, `NorthwindStringIncludeQueryMongoTest.cs`).
```

- [ ] **Step 3: Verify the EF-149 table row is still a well-formed single row**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
awk 'NR>=26 && NR<=29 {n=gsub(/\|/,"|"); print NR": pipes="n}' docs/failing-spec-tests.md
sed -n '28p' docs/failing-spec-tests.md | cut -c1-40
sed -n '28p' docs/failing-spec-tests.md | rev | cut -c1-12 | rev
```

Expected: **`pipes=5`** on all four lines (leading, three column separators, trailing) — verified as the
pre-change value for rows 26–29, so 5 is the number to match, not an assumption. Line 28 must still begin
`| [EF-149](` and still end with the count cell (`| 191 |`-shaped). If the pipe count changed you broke the
table — most likely by leaving a stray `|` in the appended prose. Fix it before committing.

- [ ] **Step 4: Commit**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git add docs/failing-spec-tests.md
git commit -m "EF-367: record the VisitChildren guard leak and correct the stale EF-X010 entry

The EF-149 row now notes that 24 of its tagged call sites in the Include suites
fail with a leaked internal EF guard (\"Calling
'ShapedQueryExpression.VisitChildren' is not allowed\") rather than EF's normal
translation-failure message. Still a genuine translation failure, so it stays
tagged EF-149; recorded because the message is scruffy and user-visible.
Owner's ruling was to record in docs only — no retagging, no new ticket.

The EF-X010 entry was wrong on two counts: those tests never used
Assert.ThrowsAsync<ContainsException>, and the entry claimed they carry no
// Fails: tag when all four do. Replaced with the measured mechanism."
```

---

### Task 3: Three-version verification and artifact preservation

**Files:**
- No repository files change. This task produces evidence and preserves it.

**Interfaces:**
- Consumes: the committed state from Tasks 1 and 2.
- Produces: a durable evidence directory and a PASS→FAIL verdict for the branch summary.

This task exists because the constraint "green on all three EF versions, compared by name" is the whole basis on
which this change can be called safe, and 130 of the 234 call sites do not even compile on EF10.

- [ ] **Step 1: Run the full specification suite on all three EF versions**

Do not restrict to the four Include classes here — the shadow is `static` and hides base overloads, so a
whole-suite run is what rules out collateral damage. Run these in the background; each pays a container boot.

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
mkdir -p "$SP/ef367"
for V in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $V"
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug $V" --no-build \
    --logger "trx;LogFileName=$SP/ef367/final-$V.trx"
done
```

Alternatively invoke the `/test-all` skill, which builds and tests all three targets in parallel. Either is
acceptable; the TRX files are what matter.

- [ ] **Step 2: Compare against the spike baselines by test name**

The spike's pre-change baselines are at
`…/scratchpad/trx/{baseline.trx,baseline-ef8.trx,baseline-ef9.trx}` (four Include classes only, all green). Use
them for the four-class comparison, and use the EF-366 preserved full-suite baselines at
`.superpowers/preserved-2026-07-31-refinclude-spike-and-EF366/trx/` for the whole-suite comparison.

Save this as `$SP/ef367/compare.py` and run it once per axis with three arguments — before-TRX, after-TRX, label:

```python
import sys, xml.etree.ElementTree as ET

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def outcomes(path):
    root = ET.parse(path).getroot()
    return {u.get('testName'): u.get('outcome') for u in root.iter(f'{NS}UnitTestResult')}


before_path, after_path, label = sys.argv[1], sys.argv[2], sys.argv[3]
before, after = outcomes(before_path), outcomes(after_path)

regressions = sorted(n for n, o in after.items() if o == 'Failed' and before.get(n) == 'Passed')
fixed = sorted(n for n, o in after.items() if o == 'Passed' and before.get(n) == 'Failed')
missing = sorted(set(before) - set(after))
newly_skipped = sorted(n for n, o in after.items() if o == 'NotExecuted' and before.get(n) == 'Passed')
unskipped = sorted(n for n, o in after.items() if o == 'Passed' and before.get(n) == 'NotExecuted')

print(f'--- {label}: before={len(before)} after={len(after)}')
print(f'    PASS->FAIL = {len(regressions)}')
for n in regressions:
    print('      REGRESSION', n)
print(f'    FAIL->PASS = {len(fixed)}')
for n in fixed:
    print('      FIXED', n)
print(f'    still failing after = {sum(1 for o in after.values() if o == "Failed")}')
print(f'    present before but ABSENT after = {len(missing)}')
for n in missing[:20]:
    print('      VANISHED', n)
print(f'    NEWLY SKIPPED (Passed->NotExecuted) = {len(newly_skipped)}')
for n in newly_skipped:
    print('      NEWLY SKIPPED', n)
print(f'    UN-SKIPPED (NotExecuted->Passed) = {len(unskipped)}')
for n in unskipped:
    print('      UN-SKIPPED', n)
```

```bash
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
python3 "$SP/ef367/compare.py" "$SP/trx/baseline.trx"     "$SP/ef367/task1-green-ef10.trx" "four-classes EF10"
python3 "$SP/ef367/compare.py" "$SP/trx/baseline-ef8.trx" "$SP/ef367/final-EF8.trx"        "EF8"
python3 "$SP/ef367/compare.py" "$SP/trx/baseline-ef9.trx" "$SP/ef367/final-EF9.trx"        "EF9"
```

The `VANISHED` check matters as much as `PASS->FAIL`: a test that stops being *collected* — because a shadow
change silently altered overload resolution and a method no longer compiles into the run — would otherwise look
like an improvement. For the EF8/EF9 axes the before-file covers only the four Include classes while the after-file
is the whole suite, so expect a large `after` count and **zero** `VANISHED`.

Expected on every axis: **`PASS->FAIL = 0`** and **`still failing after = 0`** for the four Include classes.
`FAIL->PASS` should be 0 too — this change does not fix any test, it changes what a failure means.
**`NEWLY SKIPPED = 0`** and **`UN-SKIPPED = 0`** must also hold — the project convention forbids adding skips, and
by construction the `PASS->FAIL`/`FAIL->PASS`/`VANISHED` checks above cannot detect a test that silently changed
from executed to `NotExecuted` (or back); only this pair does. If any regression, or any newly-skipped test,
appears, stop and report it; do not re-baseline anything.

- [ ] **Step 3: Confirm nothing is skipped**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
grep -c 'outcome="NotExecuted"' "$SP/ef367/final-EF10.trx" || true
git diff 44dfe5a..HEAD -- tests/ | grep -n "Skip" || echo "OK: no Skip introduced by this branch"
```

The pre-existing skips from EF-366 and upstream's unrelated `EF-352` skip may still appear in the TRX; what must
hold is that **this branch's diff adds none**.

Note: the TRX `ResultSummary/@notExecuted` attribute is unreliable — it reads `0` even when tests are actually
skipped — so `grep -c 'outcome="NotExecuted"'` above (which counts individual `UnitTestResult` elements) and the
`compare.py` NEWLY SKIPPED/UN-SKIPPED counts are the checks that matter; do not trust the summary attribute.

- [ ] **Step 4: Preserve the evidence somewhere durable**

The scratchpad is reaped. Copy the TRX files and the spike logs next to the EF-366 artifacts.

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
SP=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/3d40f247-907c-4450-85f7-ef9b234bcd62/scratchpad
DEST=.superpowers/preserved-2026-08-03-EF367-include-masking
mkdir -p "$DEST/trx"
cp "$SP"/trx/*.trx "$DEST/trx/" 2>/dev/null || true
cp "$SP"/ef367/*.trx "$DEST/trx/" 2>/dev/null || true
cp "$SP"/masklog.txt "$SP"/masklog-ef8.txt "$DEST/" 2>/dev/null || true
ls -la "$DEST" "$DEST/trx"
```

`.superpowers/` is gitignored but durable on disk, matching how the EF-366 artifacts were kept. Write a short
`$DEST/README.md` naming each file and what it proves — in particular that `masklog.txt` (208 lines, EF10) and
`masklog-ef8.txt` (460 lines, EF8) are the instrumented invocation logs whose union covers exactly 234 distinct
call sites, and that they contain only `InvalidOperationException` and `Xunit.Sdk.ThrowsException` with zero
no-throws.

- [ ] **Step 5: Report, do not commit**

Nothing to commit in this task — the repository is unchanged. Report to the controller:

- the three per-version totals (passed / failed / skipped),
- `PASS→FAIL` per axis, by name,
- the preserved-artifacts path,
- and explicitly whether the "3-version green, nothing skipped" end state from spec §5 was reached.

Do not claim green without pasting the counters you actually observed.

---

## Self-review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §4.1 delegate the four shadows, `async` form, preserve modifier order | Task 1 Step 2 |
| §4.2 baseline the 4 EF-X010 overrides with `Assert.ThrowsAnyAsync<XunitException>` | Task 1 Step 4 |
| §4.3 docs: EF-149 `VisitChildren` note | Task 2 Step 1 |
| §4.3 docs: correct the false premise | Task 2 Step 2 (the stale EF-X010 entry); the "234 sites treat wrong data as success" / "~40 masked" claims live in the controller's memory, not in the repo — the controller updates those outside this plan |
| §4.4 out of scope | No task touches `MongoSpecTestHelpers`, `src/`, or adds a ticket |
| §5 3-version verification, by name, nothing skipped | Task 3 Steps 1–3 |
| §5 preserve the spike TRX before the scratchpad is reaped | Task 3 Step 4 |
| §6 risk: overload resolution | Task 1 Step 3 (build on EF10) + Task 3 Step 1 (build on all three) |

No spec requirement is unassigned.

**Naming consistency:** `AssertNativeTranslationFailedAsync`, `AssertTranslationFailed`, `AssertMql`,
`XunitException`, `ExpressionNotSupportedException` are used identically in every task and match the real symbols
verified in the working tree.

**Known gap, stated rather than hidden:** Task 2 Step 1's instruction to append to a single long Markdown table
row is the most error-prone step in the plan, which is why Step 3 verifies the pipe count rather than trusting it.
