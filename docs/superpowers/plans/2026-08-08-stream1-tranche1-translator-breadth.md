# Stream 1, tranche 1 — translator breadth (slice 0, A2, A5) + the slice-B spike

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Ship the first tranche of EF-322 stream 1 — the mechanical `partial class` split of
`MongoExpressionTranslator`, the two lowest-risk capability-A slices (**A2** `EF.Property` leaf, **A5**
`Nullable.HasValue`/`.Value`), and the opening spike that slice B (computed sort keys) cannot be designed
without.

**Architecture:** Stream 1 is **two capabilities, not one** (spike finding 2). Capability A is expression
breadth in `MongoExpressionTranslator.TranslateOperand` / `TryResolveMember`, which predicate and
projection-leaf position reach through *literally the same method*; capability B is computed sort keys, which
is IR + lowerer + renderer work and delivers nothing on its own. This tranche delivers capability-A slices
that are **slice-B-independent** (A2 = 44 sole-cause, A5 = 36 sole-cause, neither with any slice-B exposure —
spike §5.1) and produces slice B's design input.

**Tech Stack:** C# / EF Core provider (EF8/EF9/EF10 via build configurations), xUnit (plain `Assert.*` in the
test projects — FluentAssertions is **not** referenced there), MongoDB C# driver, TestContainers
(`mongodb/mongodb-atlas-local`), JIRA via MCP.

**Plan of record this implements:** `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` §9
step 2. **Measurement it is sized from:**
`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` §3 (per-feature table), §5.1
(slice-B split), §6 (the file split), §7 (the slice list). **Entry point for context:**
`docs/native-query-status-EF-322.md` §8 + §9.8.

---

## Global Constraints

Copied from the merge plan's §8 and from the conventions this branch is held to. **Every task's requirements
implicitly include this section.**

- Rolling branch is **`NativeQueryOngoing`**. Each *slice* goes on its own branch, is squashed to ONE commit,
  and is fast-forwarded onto the rolling branch (`git checkout NativeQueryOngoing && git merge --ff-only
  <slice>`), then pushed. **Never force-push.** Keep the `-presquash` backup branch until the work merges.
  A docs-only commit (the spike findings) goes directly on `NativeQueryOngoing`.
- Commit and PR titles start with a JIRA number: `EF-1234: Description`.
- Full solution green on **EF8, EF9 and EF10**:
  `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` and the EF8/EF9 equivalents, then
  `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`.
- Both `MONGODB_URI` and `ATLAS_URI` **unset** — TestContainers boots a real `mongodb/mongodb-atlas-local`, so
  Atlas-gated tests run for real and each `dotnet test` process gets its own container.
- **NEVER pipe `dotnet test` through `tail` or `head`** — it masks the exit code and truncates per-project
  summaries. Redirect to a file and read the file.
- **Rebuild before every measurement run**, including after reverting a mutation. A task on this branch
  measured stale binaries and had to redo an entire round.
- **Zero `#if` lines added or removed in `.cs` under `src/`.** The tracked-file grep misses new files — check
  new files directly.
- **Measure wins by message TRANSITION, not by failing-name set** — a name-set diff reported 2 wins where
  there were 74 in slice 3a.
- **Classify `Assert.Throws`/`Assert.ThrowsAny` failures FIRST** when bucketing by message; the message quotes
  the inner exception and a naive substring match over-counted by 149.
- **Every guard test mutation-verified.** A test that stays green when you break the thing it names is not a
  test. Record what you mutated and how many cases went red.
- **A parameterized-`Where` leg for every functional shape.** The native `$project`/`$match` alias space and
  the driver's differ, and the route where that matters is a **late** native-factory decline under the default
  `Native` mode — reachable only with a captured local (e.g. inside `string.StartsWith`, which the native
  renderer refuses as a parameterized regex term). A constant-only `Where` never reaches it.
- **Assert VALUES, never absence-of-throw.** A read-side alias or resolution mismatch is silent for a nullable
  scalar (`null`) and for a collection (empty); only a non-nullable value type fails loudly.
- **`AssertMql` / MQL shape cannot prove a query went native.** The only proof is a
  `MongoQueryMode.NativeOnly` run that succeeds; the only proof of a decline is one that throws
  `NativeTranslationNotSupportedException`.
- Breaking changes measured by **executing against the published packages** (`v10.0.2` / `v9.1.2` / `v8.4.2`),
  never inferred from the branch. See `BREAKING-CHANGES.md` and the AGENTS.md versioning rubric.
- **Preserve each file's BOM state.** Verified for this tranche: files under
  `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/` have **no BOM**; files under
  `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` **do** have one. New files must match their
  neighbours (`head -c 3 <file> | xxd -p` — `2f2a20` is `/* ` i.e. no BOM; `efbbbf` is a BOM).
- Each subagent uses its **own uniquely-named scratchpad subdirectory** under
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/5114cfe3-e5e3-4b95-8967-8b81ee667ef9/scratchpad/`.
  Do not clean up other agents' directories. Remove any worktree you create; verify with `git worktree list`.
  Any `.claude/worktrees/agent-*` worktrees belong to other sessions — leave them alone.
- Tag every documented claim **MEASURED / CITED / INFERRED / UNVERIFIED**. This project has had six documents
  corrected for confidently-wrong claims; provenance is part of the claim.
- **Any prose count that also exists in a table must be re-summed from that table**, never restated from
  memory. Five instances of that error landed in one day on this branch, twice inside fixes for the same error.
- **Baseline (EF10), MEASURED at `e1fb753d` / `95162c86`:** default `Native` **4593 passed / 0 failed / 17
  skipped**; `MONGODB_EF_NATIVE_ONLY=1` **2427 passed / 2166 failed / 17 skipped**. Every commit since is
  docs-only, so the baseline is expected to hold at `6d38c2c7` — Task 2 **confirms** it rather than assuming it.

---

## File Structure

| File | Responsibility |
|---|---|
| JIRA `EF` project (4 new issues) | one umbrella for stream 1 + one per code slice (A2, A5, slice B) |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.cs` | **shrinks** to entry points + core dispatch (`TryTranslate`/`TryTranslateField`/`TryTranslateValue`/`TryTranslateOwnedCollectionArray`, `Unwrap`, `TranslateNode`, `TranslateComparison`, `TranslateOperand`, `TranslateValue`, the operator maps and numeric helpers). Gains `partial`. |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.Members.cs` | **new** — member resolution: `TryResolveMember`, `TryResolveOwnedFieldPath`, `TryGetMemberOrEFProperty`, `TryResolveOwnedCollectionPath`. Both A2 and A5 edit here. |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` | **new** — method-call recognizers: `MongoQuantifierKind`, `TryMatchQuantifierMethod`, `TryMatchCountExpression`, `IsCanonicalCountWithPredicate`, `ReferencesEnclosingScope`, `FreeParameterVisitor`, `UnwrapAsQueryable`, `TryMatchContainsMethod`, `TryMatchRegexMethod`, `TranslateInValues`, `GetEnumerableElementType`. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` | extended with A2 and A5 translator-level cases (its existing `Customer`, `InnerRef`/`OuterRef` two-scope and `OwnedBlog` fixtures are reused). |
| `tests/.../FunctionalTests/Query/NativeEfPropertyLeafTests.cs` | **new** — A2 end-to-end: predicate / sort / projection under `NativeOnly`, plus the parameterized-`Where` late-decline leg. |
| `tests/.../FunctionalTests/Query/NativeNullableMemberTests.cs` | **new** — A5 end-to-end, over a ragged fixture (value / explicit null / missing element). |
| `docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md` | **new** — slice B's opening spike findings. |
| `docs/native-query-status-EF-322.md` | §2 gains the tranche's slices; §8 records progress against the ≈508 checkpoint. |
| `src/.../Query/AGENTS.md` | one as-built note per code slice, in the established style. |

**Task boundaries are slice boundaries.** Tasks 2, 3 and 4 each end in a squashed commit fast-forwarded onto
`NativeQueryOngoing`; a reviewer can reject any one of them while accepting its neighbours.

---

### Task 1: File the stream-1 tickets

**Files:**
- Create: 4 JIRA issues in project `EF`
- Modify: none

**Interfaces:**
- Produces: four issue keys. Task 2 commits under the **umbrella** key; Tasks 3, 4 and 5 each use their own.

Stream 1 is the largest single item on the board (**580** cases MEASURED) and has never had a ticket. Existing
keys that must **not** be duplicated: **EF-392** (joins), **EF-393** (GroupBy), **EF-394** (composite-PK),
**EF-395** (slice 3d), **EF-396** (`Not` over an unsupported subtree), **EF-397** (stragglers), **EF-382**
(`arrayField.Contains(constant)`), **EF-247** (non-constant regex), **EF-355**, **EF-365**, **EF-375**,
**EF-380**, **EF-390**, **EF-391**.

- [ ] **Step 1: Confirm the JIRA MCP tools are reachable**

Call `mcp__jira__jira_get_issue` for `EF-391`. If it errors, the MCP server's token is missing from the session
environment — stop and report; do not create issues blind.

- [ ] **Step 2: Create the four issues**

For each row: `mcp__jira__jira_create_issue` with `project_key: "EF"`, the summary given, `issue_type: "Task"`,
and `description: "Placeholder - full description follows in an update."` **The two-call pattern is required** —
this JIRA instance stores `create_issue` descriptions raw and only converts Markdown on `update_issue`.

| # | Summary | Covers |
|---|---|---|
| 1 | `Native translation: expression breadth in MongoExpressionTranslator (stream 1)` | umbrella; slice 0 (the file split) and the spikes commit under it |
| 2 | `Native translation: EF.Property leaf in predicate, sort and projection position` | slice A2 |
| 3 | `Native translation: Nullable.HasValue / Nullable.Value` | slice A5 |
| 4 | `Native translation: computed (non-field) sort keys` | slice B — spiked in Task 5, implemented in a later plan |

- [ ] **Step 3: Fill each description via `mcp__jira__jira_update_issue`**

Each description must carry, in this order: the MEASURED case count and where it came from (issue 1: **580**
total / **474** sole-cause; issue 2: **50** total / **44** sole-cause, all three positions; issue 3: **38**
total / **36** sole-cause; issue 4: **92** cases *enabled*, **0** delivered alone, of which **74** are stream-1
sole-cause); that the counts are `MONGODB_EF_NATIVE_ONLY=1` decline-site measurements at `e1fb753d`, so they
are **coverage** not correctness (every one of these shapes falls back and returns correct results today); and
a pointer to `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` §3 and §7.

Issue 4 must additionally state that slice B is **IR + lowerer + renderer work, not a translator arm**
(`RenderSort` hard-throws for any `KeySelector` that is not a `MongoFieldExpression`, because MQL `$sort`
accepts field paths only), and that it **delivers nothing on its own**.

Use `h2.` for headings and `{code:c#}` for code — **not** Markdown `##` or triple backticks. Do not use `#` for
numbered lists; it renders as an `h1`. Use `* *(1)*` style instead.

- [ ] **Step 4: Verify each issue rendered correctly**

`mcp__jira__jira_get_issue` for each new key; read the `description` back. Confirm no literal `##`, no triple
backticks, no unintended `h1.`. Fix with a further `update_issue` if any is wrong.

- [ ] **Step 5: Commit nothing**

No repository change in this task. Record the four keys in your report — every later task needs one.

---

### Task 2: Slice 0 — split `MongoExpressionTranslator` into three partial files

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (1478 lines → ~770)
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed partial class MongoExpressionTranslator` across three files. **No signature, no
  visibility, no accessibility and no semantics change.** Every member keeps its exact name, parameter list,
  return type, `private`/`public`/`static` modifiers and XML documentation.

**Why now, and why `partial` rather than extracted types** (spike §6): ~10 of stream 1's ~20 slices all edit
`TranslateOperand`, and doing the move mid-stream would mix a 1478-line file move into a behaviour-changing
diff — the exact review shape this branch has been corrected for. Extracting *types* would be **wrong**: all
three regions read the private scope state `_entityType` / `_outerParam` / `_outerEntityType` / `_innerPrefix`,
and the by-name-retarget hazard the codebase documents at length (`TryResolveOwnedCollectionPath`'s "INHERITED
INVARIANT" remark, `ReferencesEnclosingScope`, `NativeSelectManyBinder.ReferencesParameter`) is precisely a
hazard about *which scope* a member resolves against. `partial` keeps that state private with one owner.

- [ ] **Step 1: Create the slice branch and a content fingerprint of the original**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout NativeQueryOngoing && git pull --ff-only
git checkout -b EF-<umbrella>-slice0
SCRATCH=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/5114cfe3-e5e3-4b95-8967-8b81ee667ef9/scratchpad/<your-unique-dir>
mkdir -p $SCRATCH
F=src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs
head -c 3 $F | xxd -p          # expect 2f2a20 — NO BOM. New files must match.
grep -vE '^\s*(using |namespace |$)' $F | sed 's/[[:space:]]*$//' | sort > $SCRATCH/before.txt
wc -l $SCRATCH/before.txt
```

`before.txt` is the fingerprint Step 5 compares against: every non-blank line of the file except `using`
directives and the `namespace` declaration, whitespace-normalized and sorted. A pure move leaves it unchanged
apart from the class-declaration line (which gains `partial`).

- [ ] **Step 2: Move the member-resolution region into `MongoExpressionTranslator.Members.cs`**

Create the new file with: the **exact** Apache licence header from the top of the original (lines 1–14,
byte-for-byte, **no BOM**), then the **entire `using` block copied verbatim** from the original, then
`namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;`, then:

```csharp
/// <summary>
/// <see cref="MongoExpressionTranslator"/> — member resolution. Resolves a member-access / <c>EF.Property</c>
/// chain to an <see cref="IProperty"/> and its MongoDB document path, in every position (predicate, sort key
/// and projection leaf all reach <see cref="MongoExpressionTranslator.TryResolveMember"/>).
/// </summary>
/// <remarks>
/// Split out of the single-file translator as EF-322 stream 1's slice 0 — a pure <c>partial class</c> file
/// move, no signature or semantics change. These members read the private scope state
/// (<c>_entityType</c>/<c>_outerParam</c>/<c>_outerEntityType</c>/<c>_innerPrefix</c>), which is why the split
/// is <c>partial</c> and NOT an extracted type: the by-name-retarget hazard documented on
/// <see cref="TryResolveOwnedCollectionPath"/> is exactly a hazard about which scope a member resolves against.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    // ... members moved here ...
}
```

Move these four members, **cut-and-paste verbatim including every XML doc comment and inline comment**, in this
order (original line ranges given for locating them; they are `private`, and stay `private`):

| member | original lines |
|---|---:|
| `TryResolveMember` | 763–807 |
| `TryResolveOwnedFieldPath` | 809–887 |
| `TryGetMemberOrEFProperty` | 889–912 |
| `TryResolveOwnedCollectionPath` | 1201–1285 |

Copying the whole `using` block rather than a hand-picked subset is deliberate: it makes the move provably
sufficient with no judgement call. If the build reports unused-using warnings, trim in Step 4 — not before.

- [ ] **Step 3: Move the method-call recognizers into `MongoExpressionTranslator.MethodCalls.cs`**

Same header / `using` block / namespace preamble, then:

```csharp
/// <summary>
/// <see cref="MongoExpressionTranslator"/> — method-call recognizers. Matches the LINQ method shapes the
/// translator understands (quantifiers, element counts, <c>Contains</c>, the string regex family) and the
/// scope guard that keeps a correlated element predicate out of the element-scoped translator.
/// </summary>
/// <remarks>
/// Split out of the single-file translator as EF-322 stream 1's slice 0 — a pure <c>partial class</c> file
/// move, no signature or semantics change.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    // ... members moved here ...
}
```

Move these, verbatim, in this order:

| member | original lines |
|---|---:|
| `MongoQuantifierKind` (private nested enum) | 914–922 |
| `TryMatchQuantifierMethod` | 924–989 |
| `TryMatchCountExpression` | 991–1076 |
| `IsCanonicalCountWithPredicate` | 1078–1090 |
| `ReferencesEnclosingScope` | 1092–1134 |
| `FreeParameterVisitor` (private nested class) | 1136–1185 |
| `UnwrapAsQueryable` | 1187–1199 |
| `TryMatchContainsMethod` | 1315–1360 |
| `TryMatchRegexMethod` | 1362–1406 |
| `TranslateInValues` | 1408–1455 |
| `GetEnumerableElementType` | 1457–1472 |

- [ ] **Step 4: Make the original file `partial` and confirm it kept exactly the right members**

In `MongoExpressionTranslator.cs`, change

```csharp
internal sealed class MongoExpressionTranslator
```

to

```csharp
internal sealed partial class MongoExpressionTranslator
```

and delete the moved members. What must **remain** in the original file, and nothing else: the fields, both
constructors, `TryTranslate`, `TryTranslateField`, `TryTranslateValue`, `TryTranslateOwnedCollectionArray`,
`WideningNumericConversions`, `IsWideningNumericConvert`, `ContainsIntegerDivision`, `IsIntegerType`,
`AllFieldsDefaultSerialized`, `Unwrap`, `TranslateNode`, `TranslateComparison`, `IsSimpleValue`,
`MapComparisonOperator`, `MapArithmeticOperator`, `TranslateOperand`, `IsNumericType`, `TranslateValue`,
`HasNumericConvert`, `Mirror`, `IsComparison`.

(`TryTranslateOwnedCollectionArray` stays with the other public entry points even though it delegates to
`TryResolveOwnedCollectionPath` in `Members.cs` — a cross-file call inside one `partial class` is free.)

Build all three EF versions:

```bash
for c in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" > $SCRATCH/build-$c.log 2>&1
  echo "$c=$?"; grep -cE "warning (CS|IDE)" $SCRATCH/build-$c.log
done
```

Expected: `=0` for each, and **no new warnings** versus a build of the same solution at `HEAD~`. If the copied
`using` block produces unused-using warnings, trim only the flagged directives and rebuild.

- [ ] **Step 5: Prove the move altered no line**

```bash
cat src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator*.cs \
  | grep -vE '^\s*(using |namespace |$)' | sed 's/[[:space:]]*$//' | sort > $SCRATCH/after.txt
diff $SCRATCH/before.txt $SCRATCH/after.txt
```

Expected diff: **only** the added lines — the two new files' licence headers and XML doc/class-declaration
lines, plus `internal sealed partial class MongoExpressionTranslator` replacing
`internal sealed class MongoExpressionTranslator`. **Zero removed lines and zero modified lines.** Any other
difference means a member body changed during the move; fix it before continuing.

Also confirm no `#if` churn and correct BOM state on the new files:

```bash
git diff HEAD -- src/ | grep -cE '^[+-].*#if'   # expect 0
for f in src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator*.cs; do
  printf "%s " "$f"; head -c 3 "$f" | xxd -p; done   # expect 2f2a20 for all three
```

- [ ] **Step 6: Run the full solution on all three EF versions**

```bash
for c in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $c" --no-build > $SCRATCH/test-$c.log 2>&1
  echo "$c=$?"; grep -E "Passed!|Failed!|Failed:" $SCRATCH/test-$c.log
done
```

Expected: **0 failures on each**.

- [ ] **Step 7: Confirm the baseline, on both axes**

This is also the task that discharges the Global Constraints' "confirm the baseline" obligation.

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/spec-native.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-native.log

MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=slice0.trx" --results-directory $SCRATCH \
  > $SCRATCH/spec-nativeonly.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-nativeonly.log
```

Expected, **exactly**: `Native` **4593 / 0 / 17**; `NativeOnly` **2427 / 2166 / 17**. A file move cannot move a
test — any delta is a finding to investigate, not to explain away. **Keep `slice0.trx`**: Tasks 3 and 4 compare
their `NativeOnly` failing set against it, and a "set-identical" claim is un-recheckable after the fact
without the retained TRX.

- [ ] **Step 8: Squash, fast-forward, push**

```bash
git add -A && git commit -m "wip"        # (development commits are fine; the squash is what ships)
git branch -f EF-<umbrella>-slice0-presquash HEAD
git reset --soft $(git merge-base HEAD NativeQueryOngoing)
git commit -F $SCRATCH/msg.txt
git diff --quiet EF-<umbrella>-slice0-presquash HEAD && echo "squash content-identical"
git checkout NativeQueryOngoing && git merge --ff-only EF-<umbrella>-slice0 && git push
```

`msg.txt` starts `EF-<umbrella>: split MongoExpressionTranslator into three partial files (stream 1, slice 0)`
and records: that it is a pure file move with zero behaviour change; the three-way split and why (~10 of stream
1's ~20 slices all edit `TranslateOperand`); why `partial` and **not** extracted types; and the Step 5 / Step 7
evidence (zero-line diff, 0 failures on all three EF majors, both spec axes unchanged).

---

### Task 3: Slice A2 — the `EF.Property` leaf

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs` (`TryResolveMember`)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeEfPropertyLeafTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (as-built note)

**Interfaces:**
- Consumes: Task 2's `Members.cs`.
- Produces: no new signature. `TryResolveMember(Expression node, out IProperty? property, out string? fieldPath)`
  keeps its exact shape and additionally resolves a top-level `EF.Property<T>(param, "Name")`.

**Size (MEASURED, spike §3 row 3 / §7 row A2): 50 total — pred 38 / sort 6 / proj 6 — of which 44 sole-cause,
0 slice-B-dependent.** The lowest-risk slice on the board: pure resolution, no new node kind, and because
`TryResolveMember` is reached from all three positions its result is a `MongoFieldExpression`, so the sort 6
land **without** slice B.

**Root cause (READ, spike §4.2 group 2):** `TryResolveMember`'s fast path takes only
`MemberExpression { Expression: ParameterExpression }`. Every other shape — including an `EF.Property(...)`
call — is delegated to `TryResolveOwnedFieldPath`, which collects hop names and then **requires at least two**
(`if (names.Count < 2) return false;`), because a single top-level member is supposed to have been handled by
the fast path. A top-level `EF.Property<T>(o, "Scalar")` is exactly one hop, so it falls into the gap between
the two.

- [ ] **Step 1: Write the failing unit tests**

Add to `MongoExpressionTranslatorTests` (the class already has `Customer`, the `InnerRef`/`OuterRef` two-scope
pair that deliberately share a `Name` member, and the `GetEntityType<T>` / `NewTranslator` / `PredicateBody<T>`
/ `FieldBody<T>` helpers — reuse them, do not add new ones):

```csharp
// ------------------------------------------------------------------
// EF-322 stream 1, slice A2: a top-level EF.Property leaf resolves in
// all three positions (predicate / sort key / projection value).
// ------------------------------------------------------------------

[Fact]
public void EF_Property_top_level_leaf_resolves_in_predicate_position()
{
    var entityType = GetEntityType<Customer>();
    var body = PredicateBody<Customer>(c => EF.Property<int>(c, "Age") > 21);
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
    Assert.Equal("Age", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
}

[Fact]
public void EF_Property_top_level_leaf_resolves_in_sort_position()
{
    var entityType = GetEntityType<Customer>();
    var body = FieldBody<Customer>(c => EF.Property<int>(c, "Age"));
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslateField(body, out var field));
    Assert.Equal("Age", field!.ElementName);
}

[Fact]
public void EF_Property_top_level_leaf_resolves_in_value_position()
{
    var entityType = GetEntityType<Customer>();
    var body = FieldBody<Customer>(c => EF.Property<int>(c, "Age") + 1);
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslateValue(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("Age", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
}

[Fact]
public void EF_Property_naming_an_unmapped_member_declines()
{
    var entityType = GetEntityType<Customer>();
    var body = FieldBody<Customer>(c => EF.Property<int>(c, "NotAProperty"));
    var translator = NewTranslator(entityType);

    Assert.False(translator.TryTranslateField(body, out _));
}
```

Plus the **two guards that must survive**, each written so that only the guard can make it fail:

```csharp
// A hand-built EF.Property node, so the test controls the receiver shape exactly. EF's own nav-expansion
// emits a BARE receiver, but the C# compiler may wrap a reference argument in a Convert-to-object for the
// `object entity` parameter — the implementation unwraps it (Step 3), and these two tests cover both shapes:
// this helper builds the bare form, and the C#-lambda tests above build whatever Roslyn emits.
private static MethodCallExpression EfProperty<TProperty>(Expression root, string name)
    => Expression.Call(
        typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(TProperty)),
        root,
        Expression.Constant(name));

[Fact]
public void EF_Property_on_the_outer_param_resolves_against_the_OUTER_scope_by_identity()
{
    // InnerRef and OuterRef both declare "Name", so a by-NAME resolution would silently answer with the
    // inner scope's field. Two-scope mode routes by ReferenceEquals on the parameter — and must do so for
    // the EF.Property spelling exactly as it already does for the member-access spelling
    // (cf. Two_scope_shadowed_member_name_resolves_by_parameter_identity_not_name, above).
    var innerType = GetEntityType<InnerRef>();
    var outerType = GetEntityType<OuterRef>();
    var outerParam = Expression.Parameter(typeof(OuterRef), "o");
    var innerParam = Expression.Parameter(typeof(InnerRef), "r");
    // EF.Property<string>(r, "Name") == EF.Property<string>(o, "Name")
    var body = Expression.Equal(
        EfProperty<string>(innerParam, nameof(InnerRef.Name)),
        EfProperty<string>(outerParam, nameof(OuterRef.Name)));
    var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("_lookup_Refs.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
}

[Fact]
public void EF_Property_naming_a_composite_primary_key_component_still_declines()
{
    // A composite-PK component is stored nested under "_id" and is NOT addressable by its top-level element
    // name. TryResolveMember declines it for the member-access spelling; the EF.Property spelling must
    // decline identically, or the emitted $match addresses a field that does not exist and silently
    // returns nothing.
    using var db = SingleEntityDbContext.Create<CompositeKeyed>(
        mb => mb.Entity<CompositeKeyed>().HasKey(x => new { x.KeyA, x.KeyB }));
    var entityType = db.Model.FindEntityType(typeof(CompositeKeyed))!;
    var translator = NewTranslator(entityType);
    var param = Expression.Parameter(typeof(CompositeKeyed), "c");

    Assert.False(translator.TryTranslateField(EfProperty<int>(param, nameof(CompositeKeyed.KeyA)), out _));

    // Control: a NON-key scalar on the same entity resolves, so the decline above is the composite-PK guard
    // and not a general failure of this fixture.
    Assert.True(translator.TryTranslateField(EfProperty<string>(param, nameof(CompositeKeyed.Label)), out var ok));
    Assert.Equal("Label", ok!.ElementName);
}
```

Add the composite-key fixture alongside the existing private model classes at the top of the file:

```csharp
// Composite-PK fixture: its key components are stored under "_id" and are not addressable by their own
// top-level element names, which is what TryResolveMember's composite-PK guard declines.
private class CompositeKeyed
{
    public int KeyA { get; set; }
    public int KeyB { get; set; }
    public string Label { get; set; } = "";
}
```

- [ ] **Step 2: Run them and confirm they fail for the right reason**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1 && \
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoExpressionTranslatorTests" \
  > $SCRATCH/u.log 2>&1; grep -E "Passed!|Failed!|Failed:" $SCRATCH/u.log
```

Expected: the four positive cases FAIL (`TryTranslate*` returns false — the resolution gap), and the two guard
cases PASS already (they assert a decline / a correct routing that the current code also produces). Record
which is which: a guard case that fails now would mean the fixture, not the feature, is wrong.

- [ ] **Step 3: Implement — normalize the single-hop `EF.Property` spelling into the fast path**

In `MongoExpressionTranslator.Members.cs`, replace `TryResolveMember`'s opening fast-path test:

```csharp
        // Fast path: a bare top-level member on the query parameter (p.Foo). Everything else — a member
        // rooted on another hop, or an EF.Property(...) call produced by owned-nav expansion — is delegated
        // to the owned single-reference dotted-path resolver (single-scope only), which declines cleanly
        // (returns false) for any shape that is not a valid owned chain.
        if (node is not MemberExpression { Expression: ParameterExpression param } me)
            return TryResolveOwnedFieldPath(node, out property, out fieldPath);
```

with a two-spelling normalization:

```csharp
        // Fast path: a top-level scalar access on the query parameter, in EITHER spelling EF produces —
        // a bare member (p.Foo) or the shadow-safe EF.Property<T>(p, "Foo") call. Both name ONE hop off the
        // parameter and must resolve identically; the EF.Property spelling used to fall into a gap, because
        // this method delegated it to TryResolveOwnedFieldPath, whose own `names.Count < 2` check declines a
        // single hop on the (correct) assumption that this fast path already handled it (EF-322 slice A2).
        //
        // Everything else — a member rooted on another hop, or a MULTI-hop EF.Property chain from owned-nav
        // expansion — is still delegated to the owned dotted-path resolver, which declines cleanly for any
        // shape that is not a valid owned chain.
        ParameterExpression param;
        string memberName;
        switch (node)
        {
            case MemberExpression { Expression: ParameterExpression memberParam } me:
                param = memberParam;
                memberName = me.Member.Name;
                break;

            // The EF.Property spelling, single hop only: EF.Property<T>(param, "Name"). Unwrap is applied to
            // the receiver because EF's own nav-expansion emits a BARE parameter there while the C# compiler
            // may wrap it in a Convert-to-object for EF.Property's `object entity` parameter — the two must
            // resolve identically, and Unwrap strips exactly that. A receiver that is anything else after
            // unwrapping is a MULTI-hop chain and belongs to the owned dotted-path resolver, unchanged.
            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var receiver, ConstantExpression { Value: string name }]
                     && Unwrap(receiver) is ParameterExpression callParam:
                param = callParam;
                memberName = name;
                break;

            default:
                return TryResolveOwnedFieldPath(node, out property, out fieldPath);
        }
```

Then make exactly **one** further change in the body below: `scopeType.FindProperty(me.Member.Name)` becomes
`scopeType.FindProperty(memberName)`. The scope-routing line already reads `param` and is untouched — it
stays `ReferenceEquals(param, _outerParam)`, **identity, never name**. The composite-PK guard, the
element-name lookup and the `_innerPrefix` prefixing are all untouched.

Two helpers this relies on are already in scope: `IsEFPropertyMethod()` (from
`Microsoft.EntityFrameworkCore.Infrastructure`, in the file's `using` block — the same helper
`TryGetMemberOrEFProperty` uses) and `Unwrap` (in the core partial file; a cross-file call inside one
`partial class` needs nothing).

- [ ] **Step 4: Run the unit tests and confirm all six pass**

Same command as Step 2. Expected: all six pass, and the rest of `MongoExpressionTranslatorTests` is unmoved.

- [ ] **Step 5: Mutation-verify the two guards**

Neither guard is new, but both now run on a new input, and a guard that does not discriminate is not evidence.

1. **Scope routing:** change the routing line to compare by member *name* instead of parameter identity
   (`var isOuter = _outerParam is not null && me.Member.Name == "...";` — any name-based stand-in). Rebuild,
   run the class, record how many cases go red. Revert, **rebuild**, re-run.
2. **Composite-PK:** delete the `resolved.IsPrimaryKey() && ...Properties.Count > 1` conjunct. Rebuild, run,
   record. Revert, **rebuild**, re-run.

If either mutation turns **nothing** red, the corresponding test does not discriminate — fix the test, do not
record the guard as covered.

- [ ] **Step 6: Write the functional tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeEfPropertyLeafTests.cs` — **with a BOM**,
matching its neighbours. Follow `NativeBareProjectionTests` exactly for structure: `[XUnitCollection("QueryTests")]`,
`IClassFixture<TemporaryDatabaseFixture>`, a private
`CreateContext(IMongoCollection<T> collection, MongoQueryMode mode)` built via `SingleEntityDbContext.Create`
with `new MongoDbContextOptionsBuilder(b).UseQueryMode(mode)` and
`b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))`.

Cases, each asserting VALUES:

1. `Predicate_goes_native` — `Where(c => EF.Property<int>(c, "Rank") > 1)` under `NativeOnly`, asserting the
   expected titles.
2. `Sort_key_goes_native` — `OrderBy(c => EF.Property<string>(c, "Title"))` under `NativeOnly`, asserting the
   **ordered** list. This is the case that proves the sort 6 land without slice B.
3. `Projection_leaf_goes_native` — `Select(c => new { c.Title, R = EF.Property<int>(c, "Rank") })` under
   `NativeOnly`.
4. `Parameterized_where_leg` — the mandatory late-decline leg: a captured local inside
   `string.StartsWith` alongside the `EF.Property` leaf, run under the **default `Native`** mode (this route
   does not exist under `NativeOnly`, which throws at the gate first), asserting the values are correct. Mix a
   non-nullable leaf with a nullable string and a nullable int, because only the non-nullable one fails loudly.
5. `Parity_with_driver_linq` — the same query under `Native` and `DriverLinq`, asserting identical results.
6. `Shadow_property_goes_native` — the shape `EF.Property` exists for: a property with **no CLR member**
   (`mb.Entity<T>().Property<int>("Shadow")`), read via `EF.Property<int>(c, "Shadow")`. Asserts the slice
   covers the spelling's actual purpose, not just the redundant spelling of a mapped member.
7. `Composite_key_component_declines_and_still_returns_correct_rows` — the tripwire: correct rows under
   `Native`, `NativeTranslationNotSupportedException` under `NativeOnly`.

- [ ] **Step 7: Run the functional tests, then the full solution on all three EF versions**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeEfPropertyLeafTests" > $SCRATCH/f.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/f.log

for c in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $c" > $SCRATCH/b-$c.log 2>&1
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $c" --no-build > $SCRATCH/t-$c.log 2>&1
  echo "$c=$?"; grep -E "Passed!|Failed!|Failed:" $SCRATCH/t-$c.log
done
```

Expected: 0 failures on all three.

- [ ] **Step 8: Measure the spec delta on both axes, by message transition**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/spec-native.log 2>&1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=a2.trx" --results-directory $SCRATCH > $SCRATCH/spec-no.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-native.log $SCRATCH/spec-no.log
```

- Default `Native` must stay **4593 / 0 / 17**. If an `AssertMql` baseline moved, that is a re-baseline
  decision to report with the evidence, not to make silently.
- `NativeOnly`: compare `a2.trx` against Task 2's retained `slice0.trx` by **message transition** — for every
  test, the (name → failure-message) pair before and after. Report: `Failed→Passed` count, any
  `Passed→Failed` (a regression — stop and report), and any `Failed→Failed with a different message` (a case
  that moved to its *next* blocker, which is progress but not a win).
- **Expected ≈44 wins** (the sole-cause figure). A materially lower number is a finding to report with the
  new first-decline sites, not a reason to widen the slice.

- [ ] **Step 9: Check the break rubric against the release tags**

`TryResolveMember` is `private` on an `internal sealed` class, so no public surface moves; the change is
fallback → native with unchanged results, which the rubric explicitly carves out. **Confirm rather than
assume**, per `docs/../memory` lesson "measure the tag": the shape to probe is the one slice 3a's
`BREAKING-CHANGES.md` entry already covers — a **required (non-nullable) property whose stored element is
absent**, now read through the provider's own shaper instead of the driver's lenient deserializer. If an
`EF.Property` projection leaf reaches that same class of behaviour, it is already covered by the existing
entry and needs no new one; say so explicitly in the report. Only add a `BREAKING-CHANGES.md` entry if a probe
executed **against `v10.0.2` / `v9.1.2` / `v8.4.2`** shows a value change outside that class.

- [ ] **Step 10: Write the AGENTS.md as-built note**

Add a note to `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` in the established style, recording: what is
now native (the three positions, with the MEASURED per-position counts 38/6/6); the root cause (the one-hop gap
between the fast path and `TryResolveOwnedFieldPath`'s `names.Count < 2` check); that scope routing stays
**identity, never name**, and that the composite-PK decline is unchanged; the mutation evidence from Step 5;
the measured spec delta from Step 8; that shadow properties are the spelling's real purpose; and the
`BREAKING-CHANGES.md` disposition from Step 9. Tag every number MEASURED.

- [ ] **Step 11: Squash, fast-forward, push**

Same mechanics as Task 2 Step 8, on branch `EF-<A2>`, backup `EF-<A2>-presquash`, message beginning
`EF-<A2>: resolve a top-level EF.Property leaf in predicate, sort and projection position`.

---

### Task 4: Slice A5 — `Nullable.HasValue` and `Nullable.Value`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs` (`TryResolveMember`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateNode`, for `HasValue`)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeNullableMemberTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: Tasks 2 and 3 (same method, same file).
- Produces: no new signature and **no new `MongoExpression` node kind** — `.Value` peels to the existing
  `MongoFieldExpression`, `.HasValue` becomes the existing
  `MongoBinaryExpression(NotEqual, field, MongoConstantExpression(null, property))`, which is exactly the node
  `x.A != null` already produces and which the renderer and `MongoExpressionNegator` already handle.

**Size (MEASURED, spike §3 row 6 / §7 row A5): 38 total — pred 10 / proj 28 / sort 0 — of which 36 sole-cause,
0 slice-B-dependent.**

**Root cause (READ, spike §4.2 group 4):** `x.A.Value` is a `MemberExpression` whose receiver is itself a
`MemberExpression`, so it misses `TryResolveMember`'s fast path and is handed to `TryResolveOwnedFieldPath`,
which walks the hops requiring embedded navigations (`Value` is not one) and declines.

**⚠ THIS SLICE CAN CHANGE RESULTS, UNLIKE A2 — WHICH IS WHY IT OPENS WITH A PROBE.** In-memory LINQ throws
`InvalidOperationException` for `x.A.Value` when `A` is null; a `$match`/`$project` over a null or missing
element does not. The oracle for a shape that has one is `Native == DriverLinq`, so what the peel must
reproduce is **the driver's** answer, not in-memory LINQ's. Step 1 measures it. **Do not implement first.**

- [ ] **Step 1: Probe — what does `DriverLinq` answer for a null and a missing element, per position?**

In a throwaway functional test class (deleted before the slice commits, or kept only if it becomes a real
test), seed a ragged fixture: one row with `Score = 10`, one with an explicit BSON `null` `Score`, one with the
`Score` element **absent** entirely (raw-insert it, as `NativeBareProjectionTests.SeedRagged` does). Then run,
under `MongoQueryMode.DriverLinq`, and record the exact outcome (rows, values, or exception type and message):

| # | query | position |
|---|---|---|
| 1 | `Where(x => x.Score.Value > 5)` | predicate |
| 2 | `Where(x => x.Score.HasValue)` | predicate |
| 3 | `Where(x => !x.Score.HasValue)` | predicate |
| 4 | `OrderBy(x => x.Score.Value).Select(x => x.Title)` | sort |
| 5 | `Select(x => new { x.Title, V = x.Score.Value })` | projection, non-nullable target |
| 6 | `Select(x => new { x.Title, V = (int?)x.Score.Value })` | projection, nullable target |

Record the same six under **in-memory LINQ** over the materialized whole entities, as the second reference
point. Write the results as a MEASURED table in your report.

**Decision rule, stated in advance so the answer is not rationalized after the fact:**

- Where `DriverLinq` returns values, the native implementation must return **the same values**. That is the
  bar; in-memory LINQ divergence is acceptable and is documented (there is precedent — see the EF-359 owner
  ruling in `Query/AGENTS.md`), not fixed.
- Where `DriverLinq` **throws**, native may either throw or decline, but must not silently return a different
  answer.
- If any position cannot be made to agree with `DriverLinq` by a plain peel, **narrow the slice to the
  positions that can** and report the excluded position with its measurement. The three entry points are
  already separate methods (`TryTranslate` / `TryTranslateField` / `TryTranslateValue`), so a position-scoped
  peel is available if needed. **Do not push a divergent position through.**

- [ ] **Step 2: Write the failing unit tests**

Add to `MongoExpressionTranslatorTests`, reusing `Customer` (which already has `int? NullableAge` and
`bool? NullableFlag`):

```csharp
// ------------------------------------------------------------------
// EF-322 stream 1, slice A5: Nullable<T>.Value peels to the underlying
// field; Nullable<T>.HasValue becomes the existing "!= null" node.
// ------------------------------------------------------------------

[Fact]
public void Nullable_Value_peels_to_the_underlying_field_in_predicate_position()
{
    var entityType = GetEntityType<Customer>();
    var body = PredicateBody<Customer>(c => c.NullableAge!.Value > 21);
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("NullableAge", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
}

[Fact]
public void Nullable_Value_peels_to_the_underlying_field_in_value_position()
{
    var entityType = GetEntityType<Customer>();
    var body = FieldBody<Customer>(c => c.NullableAge!.Value + 1);
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslateValue(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("NullableAge", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
}

[Fact]
public void Nullable_HasValue_becomes_the_same_node_as_an_explicit_null_comparison()
{
    var entityType = GetEntityType<Customer>();
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => c.NullableAge.HasValue), out var viaHasValue));
    Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => c.NullableAge != null), out var viaNullCheck));

    // Same operator, same field, same right-hand constant — the two spellings must be indistinguishable at
    // the IR level, which is what makes the renderer and MongoExpressionNegator correct for HasValue for free.
    var a = Assert.IsType<MongoBinaryExpression>(viaHasValue);
    var b = Assert.IsType<MongoBinaryExpression>(viaNullCheck);
    Assert.Equal(b.Operator, a.Operator);
    Assert.Equal(MongoBinaryOperator.NotEqual, a.Operator);
    Assert.Equal(
        Assert.IsType<MongoFieldExpression>(b.Left).ElementName,
        Assert.IsType<MongoFieldExpression>(a.Left).ElementName);
    Assert.Null(Assert.IsType<MongoConstantExpression>(a.Right).Value);
}

[Fact]
public void Negated_HasValue_renders_to_a_form_that_selects_null_AND_missing()
{
    var entityType = GetEntityType<Customer>();
    var translator = NewTranslator(entityType);

    Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => !c.NullableAge.HasValue), out var result));

    // Pin the RENDERED form, not the node kind: what matters is that the emitted query selects both a stored
    // null and a MISSING element, which is what LINQ's !HasValue means. `$not` over `$ne: null` does, because
    // $eq/$ne partition every BSON value INCLUDING missing (the rule MongoExpressionNegator's own remarks
    // state, and the reason equality may be inverted where the four relational operators may not).
    var rendered = new MongoQueryLanguageRenderer().Render(result!, new PlaceholderTable());

    Assert.Equal(
        BsonDocument.Parse("{ 'NullableAge' : { '$not' : { '$ne' : null } } }"),
        rendered.AsBsonDocument);
}
```

If the renderer emits a **different but equivalent** form (e.g. it folds to `{ 'NullableAge' : null }`), pin
that form instead and record in the test comment why it is the exact complement — do not weaken the assertion
to a type check.

If Step 1's probe narrowed the slice, drop the corresponding tests and say so in the report.

- [ ] **Step 3: Run them and confirm they fail**

Same command as Task 3 Step 2. Expected: all four fail — `TryTranslate*` returns false.

- [ ] **Step 4: Implement the `.Value` peel**

At the top of `TryResolveMember` in `Members.cs`, before the fast-path switch:

```csharp
        // Peel Nullable<T>.Value: `x.A.Value` is a MemberExpression whose RECEIVER is the member access we
        // actually want, so without this it misses the fast path below and is handed to the owned dotted-path
        // resolver, which walks hops requiring embedded navigations and declines (EF-322 slice A5). The peel is
        // safe because the resolved property keeps its own nullability — `.Value` changes the CLR type, never
        // the stored element — so the emitted field ref is identical to the one `x.A` produces.
        while (node is MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } nullableReceiver }
               && Nullable.GetUnderlyingType(nullableReceiver.Type) is not null)
        {
            node = nullableReceiver;
        }
```

The `Nullable.GetUnderlyingType(...) is not null` conjunct is load-bearing and is **not** a redundant sibling of
the name test: a user type may declare its own member called `Value`, and peeling that would resolve the
receiver instead of the member — the wrong field, silently. (Same shape of reasoning as
`ClassifyJoinHop`'s `IsTransparentIdentifierType` conjunct, recorded in `Query/AGENTS.md`.)

- [ ] **Step 5: Implement the `HasValue` arm**

In `TranslateNode` (`MongoExpressionTranslator.cs`), add an arm **before** the `default:` bare-boolean-member
case:

```csharp
            // --- Nullable<T>.HasValue (EF-322 slice A5) ---
            //
            // Deliberately built as the SAME node an explicit `x.A != null` produces, rather than a new node
            // kind: MongoQueryLanguageRenderer already renders it, MongoExpressionNegator already inverts it
            // exactly ($eq/$ne partition every BSON value INCLUDING missing and null — see the negator's own
            // remarks for why that is true of equality and false of the four relational operators), so `!HasValue`
            // needs no code of its own. The rendered form selects null AND missing, which is what LINQ's
            // HasValue means for a stored element that is absent.
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath))
                    return null;

                return new MongoBinaryExpression(
                    MongoBinaryOperator.NotEqual,
                    new MongoFieldExpression(nullableProperty, nullablePath),
                    new MongoConstantExpression(null, nullableProperty));
            }
```

- [ ] **Step 6: Run the unit tests and confirm all four pass**

Same command as Step 3. Expected: all pass, rest of the class unmoved.

- [ ] **Step 7: Mutation-verify the two new conjuncts**

1. Delete the `Nullable.GetUnderlyingType(...) is not null` conjunct from the `.Value` peel and add a fixture
   whose entity declares its own `SomeWrapper Value { get; set; }`-style member — without the conjunct the peel
   must resolve the **wrong** field, with it the shape must decline. Record the red count both ways. If no
   fixture in the tree discriminates it, **build one** (this is the case `Query/AGENTS.md` records as the right
   way to pin exactly this class of conjunct); if you choose not to, say so explicitly and say why, rather than
   recording the conjunct as covered.
2. Change the `HasValue` arm's operator from `NotEqual` to `Equal`. Expected: the `HasValue` functional cases go
   red. Revert, **rebuild**, re-run.

- [ ] **Step 8: Write the functional tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeNullableMemberTests.cs` — **with a BOM**.
Fixture: **ragged and un-masked**, three states per nullable property — a value, an explicit BSON `null`, and a
**missing** element (raw-inserted). Cases:

1. `Value_in_a_predicate_goes_native` — `NativeOnly`, asserting the rows.
2. `Value_in_a_projection_goes_native` — `NativeOnly`, asserting the values **including** what the ragged rows
   produce, exactly as Step 1's probe measured for `DriverLinq`.
3. `HasValue_and_negated_HasValue_go_native` — `NativeOnly`, asserting both directions partition the fixture
   (every row is in exactly one of the two result sets — a property that is only true if missing and null are
   both handled).
4. `Parity_with_driver_linq_over_the_ragged_fixture` — the same queries under `Native` and `DriverLinq`,
   asserting identical results **for every ragged state**. This is the slice's real gate.
5. `Parameterized_where_leg` — the mandatory late-decline leg (captured local in `string.StartsWith` alongside
   a nullable leaf), default `Native` mode, asserting values.
6. Whichever position Step 1 excluded, if any — a tripwire asserting it still declines and still returns
   correct results.

- [ ] **Step 9: Run functional, then the full solution on all three EF versions**

As Task 3 Step 7, with `--filter "FullyQualifiedName~NativeNullableMemberTests"`. Expected: 0 failures on
EF8/EF9/EF10.

- [ ] **Step 10: Measure the spec delta on both axes**

As Task 3 Step 8, logging to `a5.trx` and comparing against `a2.trx` by **message transition**. Default
`Native` must stay **4593 / 0 / 17**. **Expected ≈36 wins.** Report any `Passed→Failed` immediately — for this
slice specifically, a regression is the *expected* failure mode if the peel diverges from the driver, so treat
it as a stop-and-report, not as a re-baseline.

- [ ] **Step 11: Check the break rubric against the release tags**

Same procedure and same reasoning as Task 3 Step 9 — but note this slice has a second candidate: if Step 1
measured that a released package returns a value where HEAD now throws (or vice versa) for a ragged nullable,
that IS observable and needs an entry. Probe by **executing against the published packages**, never by reading
the branch.

- [ ] **Step 12: Write the AGENTS.md as-built note**

Record: what is now native, with the MEASURED per-position counts (pred 10 / proj 28); that `.Value` is a peel
and `HasValue` reuses the `!= null` node, so no new node kind and no renderer/negator change; **the Step 1
probe table in full** (this is the expensive-to-re-derive fact — the driver's answer for null and missing per
position); the in-memory-LINQ divergence and its disposition; the two mutation results from Step 7; and any
position the slice deliberately excluded.

- [ ] **Step 13: Squash, fast-forward, push**

As Task 2 Step 8, on branch `EF-<A5>`, message beginning
`EF-<A5>: translate Nullable.Value and Nullable.HasValue`.

---

### Task 5: Slice B's opening spike — does a synthetic `$set` sort field survive the shapers?

**Files:**
- Create: `docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md`
- Modify (throwaway worktree only): a `MongoAddFieldsStage` prototype + `RenderSort`/lowerer/populator edits
- Read: `MongoPipelineFactory.RenderSort`, `MongoSelectLowerer`, `NativeSlotPopulator` (the `OrderBy`/`ThenBy`
  arms), `MongoStreamingEntityMaterializerRewriter`, `MongoProjectionBindingRemovingExpressionVisitor`,
  `Stages/MongoVectorSearchScoreStage.cs`

**Interfaces:**
- Consumes: spike §4.3 and §7 slice B.
- Produces: the findings slice B's design and implementation plan are written from. **Slice B cannot be
  planned before this.**

**The question, stated exactly as the stream-1 spike left it (its §9, first bullet, UNVERIFIED):** *whether a
synthetic `$set` sort field survives the whole-entity DOM and streaming shapers untouched.* Slice B enables
**92** cases (floor; ceiling 98) and delivers **0** on its own, and the merge plan's counterfactual without it
is **≤3257/4075 = ≤79.9%** — **below the 3260 bar**. So this is not an optimisation: it is load-bearing for
the merge bar, and it is the one piece of stream 1 that is not translator breadth at all.

**What is already established, so the spike does not re-derive it** (READ, spike §4.3):
`MongoPipelineFactory.RenderSort` hard-throws for any `MongoOrdering.KeySelector` that is not a
`MongoFieldExpression`, because MQL `$sort` accepts field paths only — the IR is already general
(`MongoOrdering.KeySelector` is typed `MongoExpression`). `NativeSlotPopulator`'s `OrderBy`/`ThenBy` arms call
`translator.TryTranslateField` and mark the query non-native when it returns false.

- [ ] **Step 1: Create a throwaway worktree**

```bash
SCRATCH=/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/5114cfe3-e5e3-4b95-8967-8b81ee667ef9/scratchpad/<your-unique-dir>
mkdir -p $SCRATCH
git worktree add $SCRATCH/wt NativeQueryOngoing
git worktree list   # note which worktrees existed before; remove ONLY yours at the end
```

- [ ] **Step 2: Read the three candidate emission sites and record which one a `$set` must use**

`MongoSelectLowerer.AppendSelectOpStages` walks `select.PipelineOps` and emits each op **verbatim, in arrival
order** (EF-347) — there is no fixed canonical order to slot a `$set` into. A computed sort key therefore needs
`$set` → `$sort` → `$unset` to be **adjacent and correctly ordered inside that op sequence**, not appended
elsewhere in `Lower`. Record, by reading: where the three stages must be produced (in the `MongoSortOp` arm of
`AppendSelectOpStages`, or by the populator recording extra ops, or by a composite stage), and what each choice
costs. `Stages/MongoVectorSearchScoreStage.cs` is the closest precedent for a marker stage that renders to
`$addFields` — note whether it is reusable or only analogous.

- [ ] **Step 3: Build the smallest prototype that can answer the question**

In the worktree only: a `MongoAddFieldsStage` (or the reuse Step 2 chose), a `RenderSort` arm that accepts a
non-field `KeySelector` by sorting on the synthetic field name, the lowerer wiring, an `$unset` of the
synthetic field, and `NativeSlotPopulator`'s `OrderBy` arm calling `TryTranslate`/`TryTranslateValue` when
`TryTranslateField` returns false. **Correctness of the prototype is not the deliverable** — it exists to make
the question executable. Do not tidy it, and do not carry it back.

Pick a synthetic field name and record why. It must not collide with a real element name; note whether the
existing precedents (`__score`, `MongoReplaceRootStage.OwnerKeyField` = `"__ownerKey"`,
`MongoReplaceRootStage.OrdinalField` = `"__ord"`) imply a convention, and note that the `$mergeObjects`
sentinel-collision guard on the owned bare-element path exists precisely because a same-named real field is
silently overwritten there.

- [ ] **Step 4: Answer the question, per shaper, by EXECUTION**

Run a computed-sort query — e.g. `OrderBy(x => x.A ?? x.B)` — against a real server, in each of these shapes,
and record the outcome (values, order, exception):

| shape | why |
|---|---|
| whole entity, **streaming-eligible** | the one-pass materializer's forward name-dispatch must `SkipValue()` the synthetic field. That base case is exercised by `__score` today (see `NativeVectorSearchTests.Whole_entity_vector_search_streams_and_skips_the_score_field`) — assert `StreamingEligibility.IsEligible` directly as a PREMISE, do not assume it. |
| whole entity, **DOM** (e.g. a TPH or owned-collection-element entity) | the other materialization path |
| a **projection** (`Select(x => new { ... })`) after the computed sort | the `$project` must not leak or lose the synthetic field |
| **paging** after the computed sort (`Skip`/`Take`) | order must survive the stage ordering |
| a **tracking** query | change-tracker identity resolution over an entity carrying an extra element |

Then answer explicitly: **is the `$unset` required, or does the shaper ignore an unmapped element anyway?** If
`$unset` turns out to be unnecessary for both shapers, say so — it is one stage fewer per query, and the
finding belongs in the report either way.

- [ ] **Step 5: Establish the cost of the `ThenBy` arm and of a MIXED sort**

A sort with one field key and one computed key, in both orders. Does the `$set` have to be emitted once for the
whole `MongoSortStage`, or per ordering? Does a field key still render as a plain path (keeping the sort
index-usable) while only the computed key goes through `$set`? Index-usability is worth recording: the branch
has an explicit index-first dialect preference for predicates, and the `All`-quantifier note records a measured
COLLSCAN trade-off. Measure with a `queryPlanner` explain rather than asserting.

- [ ] **Step 6: Size the slice against the measured population**

From the stream-1 spike's §4.3 table, the 92 break down as `??` 22, `Not` 18, `Contains` 18, `?:` 10, bare
const 10, `Convert`-over-comparison 4, `Add` 4, other operator 4, constructed value 2. For each, say whether
the prototype's `$set` path would carry it **once the corresponding capability-A slice ships**, or whether it
needs something further. Also settle, if the prototype makes it cheap to do so, the **6 ambiguous** cases §4.3
could not classify (`Convert`-node 4, other member 2) — resolving them needs exactly the `RenderSort` mutation
this prototype already performs, and it would replace the UNVERIFIED 92-vs-98 range with a measurement.

- [ ] **Step 7: Remove the worktree and confirm the tree is clean**

```bash
git worktree remove --force $SCRATCH/wt
git worktree list    # expect exactly the worktrees you noted in Step 1
git status --short   # expect only the new findings doc
```

- [ ] **Step 8: Write the findings doc**

`docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md`, following the shape of
`2026-08-07-stream1-translator-breadth-spike.md`: the tagging convention stated up front and applied strictly
(**MEASURED / READ / INFERRED / UNVERIFIED**), a headline findings list ordered by how much each changes slice
B's plan, the per-shaper execution table from Step 4, the `ThenBy`/mixed-sort answer from Step 5, the
index-usability measurement, the sizing from Step 6, a proposed task split with the emission-site decision from
Step 2 and its rationale, and an explicit "what is UNVERIFIED" section.

**If the prototype shows a synthetic sort field does NOT survive a shaper, that is the headline finding and
must be reported as one** — it would make slice B substantially larger than the merge plan assumes, and the
merge plan's own §7 says the checkpoint exists exactly so that kind of surprise changes the plan rather than
being absorbed silently.

- [ ] **Step 9: Commit (docs-only, directly on the rolling branch)**

```bash
git checkout NativeQueryOngoing
git add docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md
git commit -m "EF-<sliceB>: spike the computed-sort-key capability for stream 1 slice B"
git push
```

---

### Task 6: Tranche record — status doc and the running checkpoint figure

**Files:**
- Modify: `docs/native-query-status-EF-322.md` (§2 slice table, §8 bottom line)
- Modify: none under `src/`

**Interfaces:**
- Consumes: Tasks 3, 4 and 5's measured outcomes.
- Produces: a status doc a resuming agent can plan the next tranche from.

- [ ] **Step 1: Add the tranche's slices to §2**

One row each for slice 0, A2 and A5: the commit SHA **on this branch** (`git log --oneline
upstream/main..HEAD`), the measured `NativeOnly` win count, the spec delta on the default axis, and the
`BREAKING-CHANGES.md` disposition. Follow the existing table's columns; do not invent new ones.

- [ ] **Step 2: Update §8 with the running position against the checkpoint**

State: the new `NativeOnly` triple; the cumulative stream-1 wins so far; and that the checkpoint expects
**≈508 after ALL of stream 1 with slice B**, **≈400 without it**, and **≈570 after streams 1 and 2 together** —
and that **≈508 at the post-stream-1 checkpoint is success, not shortfall**. Do **not** judge the tranche
against 588; that is the superseded cited figure, and judging against it is a trap that already shipped in
these docs once and was caught in review.

**Re-sum every count from the §2 table you just wrote; do not restate a number from a report.**

- [ ] **Step 3: Verify no test movement**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > /tmp/b.log 2>&1; echo "build=$?"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/nat.log 2>&1; grep -E "Passed!|Failed:" $SCRATCH/nat.log
```

Expected: `Failed: 0, Passed: 4593, Skipped: 17`. This task is docs-only; any movement is a red flag.

- [ ] **Step 4: Commit and push**

```bash
git add docs/native-query-status-EF-322.md
git commit -m "EF-<umbrella>: record stream 1 tranche 1 in the status doc"
git push
```

---

## What comes after this plan

**Slice B's implementation plan is written from Task 5's findings** — it cannot be written before them, for the
same reason stream 1's decomposition could not be written before its own spike: doing so would mean inventing
an emission site and a shaper contract.

Then, per the merge plan's §9 sequence, the rest of stream 1 as further tranches. The measured slice-B-independent
tranche is **A1, A2, A4, A5 = 158 sole-cause** (spike §5.1) — this plan ships **A2 + A5 = 80** of it, leaving
**A1** (casts, 56 sole-cause, of which 6 need slice B — the highest single yield, and the one whose narrowing
guard must **not** simply be relaxed) and **A4** (the reverted tier 2, 28 sole-cause, which has a recorded
prerequisite: the late-fallback path must be able to emit `$ifNull` itself rather than inheriting the driver's
bare `$size` — see the step-3a note in `Query/AGENTS.md`; **do not re-attempt A4 without that fixed**).

After all of stream 1: the **mandatory re-measurement checkpoint** (merge plan §7), then stream 3 (slice 3b,
which must FIX EF-356), then stream 4 (EF-375, spike first), then stream 2, then the architecture record and
the final measurement.
