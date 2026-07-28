# Make the Include specification suites fail on wrong data — design spec

**Branch:** `EF-367`, based on `EF-366` @ `44dfe5a` (owner's choice: stack on the unmerged EF-366 so its
paging-guard fix is in the base and the driver-3.10 wrong-data bug does not pollute the triage) · driver 3.10.0 ·
**JIRA: EF-367.**

**Status:** design for review. No change has been made to `src/` or `tests/` on this branch.

**Scope:** test infrastructure only. Nothing in `src/`.

---

## 1. The defect

Four specification-test suites each declare their own shadow of `AssertTranslationFailed`:

| File | Lines |
|---|---|
| `tests/.../SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs` | 1317–1329 |
| `tests/.../SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs` | 1201–1213 |
| `tests/.../SpecificationTests/Query/NorthwindStringIncludeQueryMongoTest.cs` | 1324–1336 |
| `tests/.../SpecificationTests/Query/NorthwindEFPropertyIncludeQueryMongoTest.cs` | 1323–1335 |

The bodies are byte-identical (once `protected static new` in the String suite is normalised to
`protected new static`):

```csharp
protected new static async Task AssertTranslationFailed(Func<Task> query)
{
    try { await query(); }
    catch { return; }
    throw new Xunit.Sdk.XunitException("Expected query to fail but it succeeded.");
}
```

The bare `catch` cannot distinguish a translation failure from the base test's result-mismatch assertion, which
is just another exception. The only outcome it can fail on is "the query succeeded and returned something". There
are **234 call sites** across the four suites (62 / 62 / 62 / 48), every one of the form
`await AssertTranslationFailed(() => base.X(async));` with a preceding `// Fails:` tag.

Every other spec suite routes through `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync`
(`tests/.../SpecificationTests/Query/MongoSpecTestHelpers.cs:46-67`), which accepts
`NativeTranslationNotSupportedException` / `InvalidOperationException` / `ExpressionNotSupportedException` plus
caller-supplied types, and lets everything else — including `XunitException` — propagate. Wrong data fails there.

Five sibling suites already delegate their local shadow to that helper in one line
(`NorthwindSetOperationsQueryMongoTest.cs:933`, `NorthwindFunctionsQueryMongoTest.cs:2477`,
`NorthwindAggregateOperatorsQueryMongoTest.cs:2333`, `NorthwindMiscellaneousQueryMongoTest.cs:5197`,
`NorthwindGroupByQueryMongoTest.cs:2572`). The bare-catch species exists **only** in these four Include suites; a
sweep of `tests/` found no other instance (the four look-alikes are all typed, exception-filtered, or have the
success path asserted).

## 2. What the Task-0 spike measured, including where it contradicted the brief

Everything in this section was measured on this base commit and re-derived independently by the controller from
the raw TRX and instrumentation logs. **It contradicts the premise this work was scheduled on, and that matters
more than the fix does.**

### 2.1 Headline: zero of the 234 call sites mask a wrong-data failure

Method: baseline the four classes as-is; make the shadow strict by delegating to the shared helper; re-run; diff
**by test name**, not by count.

| Config | Baseline | Strict | New reds |
|---|---|---|---|
| EF10 | 952 passed / 0 failed | 944 / **8** | +8 |
| EF8 | 944 / 0 | 936 / **8** | +8 |
| EF9 | 944 / 0 | 936 / **8** | +8 |

The three red sets are identical by name: `Include_collection_with_client_filter`, one method × async
True/False, in each of the four suites. That is the **already-documented EF-X010**
(`docs/failing-spec-tests.md`), and all four overrides already carry its `// Fails:` tag. Nothing new; nothing
wrong-data.

Classification of the 8: **(a) wrong data = 0 · (b) query succeeded = 0 · (c) accept-list too narrow = 8 ·
(d) other = 0.**

### 2.2 The coverage fact that explains the prior over-estimate

The 234 sites are **not all live in any one configuration**:

| Guard | Sites | Live on |
|---|---:|---|
| `#if EF8 \|\| EF9` | 130 | EF8, EF9 |
| `#if !EF8 && !EF9` | 4 | EF10 |
| unconditional | 100 | all three |

So an EF10-only run compiles just **104 of 234**. The measurement therefore ran all three versions. An
instrumented pass logging the type and message of every invocation confirms coverage is exactly complete: EF10
logged 104 distinct class+method pairs × 2 async = 208 invocations; EF8 logged 230 × 2 = 460; the union is
**exactly 234** distinct pairs. Every call site was exercised.

A prior estimate of "~40 masked failures" was recorded but never controller-verified. It does **not** hold. It is
most consistent with an EF10-only view of the suites.

### 2.3 Why the negative claim is trustworthy

Because the shared helper accepts *any* `InvalidOperationException`, a narrow accept-list could in principle hide
wrong data behind it. The instrumented pass rules that out. Across all 668 invocations on both axes there are
exactly **two** exception types:

| Type | Count | Message shape |
|---|---:|---|
| `System.InvalidOperationException` | 652 | 556 × EF `CoreStrings.TranslationFailed` ("could not be translated"); 96 × `"Calling 'ShapedQueryExpression.VisitChildren' is not allowed"` |
| `Xunit.Sdk.ThrowsException` | 16 | the EF-X010 reds (8 per axis) |

**Zero no-throw.** Not one of the 234 queries executes successfully, so there is no wrong data to return.

The 96 `VisitChildren` invocations resolve to **24 call sites** — 6 distinct method names, each overridden in all
four suites, × async True/False, × the two measured axes. All 6 are `*GroupBy_Select` shapes and all 24 sites are
unconditional (they appear identically in the EF10 and EF8 logs):
`Include_collection_GroupBy_Select`, `Include_collection_Join_GroupBy_Select`, `Include_reference_GroupBy_Select`,
`Include_reference_Join_GroupBy_Select`, `Join_Include_collection_GroupBy_Select`,
`Join_Include_reference_GroupBy_Select`.

### 2.4 The accidental guard, and why it is not a defence

226 of the 234 sites are followed by a bare `AssertMql()`, which asserts that **no MQL was logged at all**
(`tests/.../SpecificationTests/Utilities/TestMqlLoggerFactory.cs:44-55` → `Assert.Empty`). It runs after the bare
catch swallows, so at those sites a query that had executed and returned wrong data would still be caught — by
the MQL assertion rather than by the helper. Only 8 sites lack that net: the 4 EF-X010 sites, which deliberately
baseline a non-empty `Customers.` entry, and 4 in an `#if EF8 || EF9` branch with no MQL assertion.

This is worth stating precisely because it is easy to over-credit. Given §2.3, the reason nothing is masked today
is the simpler one — every query fails to translate outright. The `AssertMql()` net is real but incidental, and
it is **not** load-bearing today.

It does, however, decide the sequencing argument. A test asserting "no MQL was emitted" is an accidental guard,
not a designed one, and it **disappears the moment a slice legitimately starts emitting MQL at these sites** —
which is exactly what the planned native reference-`Include` slice does. At that point the four suites would have
no wrong-data detection at all, in the suites that slice verifies itself against.

## 3. The honest case for doing this

It does **not** fix any currently-hidden failure; there are none. What it buys:

1. A helper that structurally cannot tell wrong data from a translation failure is removed before the
   reference-`Include` slice dissolves the accidental net described in §2.4.
2. The four suites become as trustworthy as every other spec suite, closing a split that has already cost
   something real once: the driver-3.10 silent-wrong-data regression (EF-366) surfaced in
   `NorthwindGroupByQueryMongoTest` *because* that suite uses the strict helper. Had the same regression landed
   in an Include shape, §2.4 already tells us what would have happened: at 226 of the 234 sites the accidental
   `AssertMql()` net would have caught it too, so only the 8 netless sites (the 4 EF-X010 sites plus the 4
   `#if EF8 || EF9` sites with no MQL assertion) would have swallowed it and stayed green.
3. It is cheap — 4 one-line replacements plus 4 test-override rewrites.

This is insurance, not excavation. The claim that it **blocks** the joins work is not supported by the
measurement and should not be repeated.

## 4. Design

### 4.1 Delegate the four shadows to the strict helper

```csharp
protected new static async Task AssertTranslationFailed(Func<Task> query)
    => await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(query);
```

This is the exact signature the spike compiled and ran on all three EF versions — deliberately keeping `async` /
`await` rather than the tidier expression-bodied `Task`-returning form, so the shipped change is the measured one
and overload resolution against the hidden base overloads is not re-litigated on assumption.

**Preserve each file's existing modifier order:** three suites declare `protected new static`, while
`NorthwindStringIncludeQueryMongoTest.cs` declares `protected static new`. Both are legal and the difference is
cosmetic; leaving each as-is keeps the diff to the method body alone.

All 234 call sites stay untouched. The default accept-list needs **no widening**, and that is measured rather
than hoped: per §2.3 every invocation throws `InvalidOperationException`, which the helper already accepts.

Two rejected alternatives, both rejected on measurement:

- **Delete the shadows** and let the call sites bind to EF's base
  `QueryTestBase<TFixture>.AssertTranslationFailed`. It compiles, but the base asserts exact
  `InvalidOperationException` **and** `Assert.Contains` on the `CoreStrings.TranslationFailed` tail. The
  `VisitChildren` sites carry a different message and so would fail that `Contains`, giving **56 red cases per
  EF version** (48 `VisitChildren` invocations + the 8 EF-X010) instead of 8.
- **Keep a local body** that filters exception types inline. Pointless duplication: the shared helper already
  does exactly this, five sibling suites already delegate to it, and a local copy is a fresh place for the same
  class of bug to reappear.

### 4.2 Baseline the 4 EF-X010 overrides honestly

At `Include_collection_with_client_filter` in each of the four suites:

```csharp
// Fails: Throws with Mongo-specific message rather than the generic EF message. EF-X010
await Assert.ThrowsAnyAsync<XunitException>(() => base.Include_collection_with_client_filter(async));
AssertMql(
    """
Customers.
""");
```

The mechanism, which is why these cannot be served by the strict helper: the upstream base test does its own
`Assert.Contains(..., await Assert.ThrowsAsync<InvalidOperationException>(...))`
(`efcore/test/EFCore.Specification.Tests/Query/NorthwindIncludeQueryTestBase.cs`), and the provider throws the
driver's `ExpressionNotSupportedException`. So what escapes `base.X` is an `Xunit.Sdk.ThrowsException` — precisely
the category the strict helper exists to reject. The failure is a wrong *exception type*, not a translation
failure, and the helper should keep rejecting it.

`Assert.ThrowsAnyAsync<XunitException>` is the project's recorded convention for baselining current-incorrect
behaviour without `Skip`: when the provider starts throwing the expected type, the base assertion passes, the
`ThrowsAnyAsync` fails, and the test demands a real re-baseline. No `[Skip]` is introduced anywhere in this work.

Two rejected alternatives:

- **Teach the shared helper to unwrap a nested `ThrowsException`** whose inner exception is an accepted type.
  Narrower than it first appears — a result-mismatch `XunitException` has no inner exception, so wrong data would
  still fail — but it loosens a helper shared by every other suite in order to serve 4 sites, and it hides the
  fact that the provider throws the wrong exception type. Rejected.
- **Assert `ExpressionNotSupportedException` directly**, abandoning the `base.X` call and inlining the query.
  Most precise about actual behaviour, but it breaks the spec-suite pattern that every override delegates to
  base, so the test stops tracking upstream changes. Rejected.

### 4.3 Docs

- `docs/failing-spec-tests.md`: extend the existing **EF-149** row with the §2.3 finding — 24 call sites across
  the four Include suites (the 6 `*GroupBy_Select` method names, each overridden in all four) fail with the leaked
  internal guard `"Calling 'ShapedQueryExpression.VisitChildren' is not allowed"` rather than EF's normal
  translation-failure message. **No retagging and no new ticket** (owner's ruling: record in docs only).
- Correct the false premise wherever it is recorded, so "234 sites treat wrong data as success" and the "~40
  masked failures" figure stop propagating. Replace both with §2.1–§2.3.

### 4.4 Explicitly out of scope

Per the owner's "fix it now, small" ruling:

- No general hardening to stop a future bare catch being introduced.
- No audit of the `AssertMql()`-as-accidental-guard reliance across other suites.
- No provider change to make EF-X010 throw EF's exception type. (Also: per the versioning rubric, the exception
  type of an **unsupported** operation is not contract, so this is not a break either way.)
- No new ticket for the `VisitChildren` internal-guard leak.
- No change to the 130 `#if EF8 || EF9` call sites beyond what §4.1 gives them for free.

## 5. Verification

- Full **3-version** run (EF8 / EF9 / EF10). A single-version run is insufficient and §2.2 is the reason.
- `PASS→FAIL = 0` established by comparing **test names** parsed from raw TRX, not by comparing counts.
- Baselines: the spike's TRX, preserved at
  `…/scratchpad/trx/{baseline,strict,instrumented}{,-ef8,-ef9}.trx`, to be copied somewhere durable before the
  scratchpad is reaped.
- Expected end state: 3-version **green, nothing skipped**. The 8 EF-X010 cases pass via §4.2 rather than via a
  swallowed exception.
- Because the four shadows are `static` and hide all base overloads, confirm the build is clean on all three
  configurations. This is expected to be a formality rather than a real risk: §4.1 ships the exact signature the
  spike compiled and ran green on EF8, EF9 and EF10.

## 6. Risks

| Risk | Assessment |
|---|---|
| The strict helper's accept-list is too narrow for some site not exercised by the spike | Ruled out: §2.2 proves all 234 sites were exercised and §2.3 enumerates every exception type that occurred. |
| The shadow's new signature changes overload resolution | Eliminated. §4.1 keeps the `async` signature the spike compiled and ran green on all three EF versions; only the method body changes. |
| The 4 EF-X010 rewrites drift from upstream | Accepted and mitigated: they still call `base.X`, so an upstream change to the base test still reaches them. |
| Future regression re-masked because a *new* Include test uses a bare catch | Accepted, out of scope (§4.4). The species now exists nowhere in `tests/`, so a reintroduction would be a visibly novel pattern in review. |
