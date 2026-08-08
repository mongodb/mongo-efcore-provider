# Slice A1 — cast / `Convert` breadth

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Fix a live silent-wrong-order defect in cast-bearing sort keys, and make numeric casts native in
comparison, sort and projection position — **30 specification cases**, the largest remaining single item in
EF-322 stream 1.

**Architecture:** A1 is **not** the slice every prior document describes. The yield lives at a third decline
site — `TranslateComparison`'s query-native branch and its private `HasNumericConvert` guard — not at
`TranslateOperand`'s `Convert` branch, which those documents all point at and which delivers **0 of the 28**.
The work is: an order-aware unwrap for sort keys; one new expression node (`MongoConvertExpression`, rendering
`$toInt`/`$toLong`/`$toDouble`/`$toDecimal`); a widening relaxation at the comparison site carrying a
**conditional constant-serialization rule** without which it ships silent wrong data; an enum/char/boxing arm;
and a projection-leaf gate.

**Tech Stack:** C# / EF Core provider (EF8/EF9/EF10 via build configurations), xUnit with plain `Assert.*`
(FluentAssertions is **not** referenced in the test projects), MongoDB C# driver, TestContainers
(`mongodb/mongodb-atlas-local`).

**Ticket:** file one under epic **EF-322** as Task 1 below. **Written from:**
`docs/superpowers/specs/2026-08-08-a1-cast-breadth-spike.md` — every figure and design decision below is that
spike's, and its section numbers are cited so the reasoning is one hop away. **Branch tip when this plan was
written:** `4f77e892`.

---

## Global Constraints

**Every task's requirements implicitly include this section.**

- Rolling branch is **`NativeQueryOngoing`**. This slice goes on its own branch, is squashed to ONE commit, and
  is fast-forwarded onto the rolling branch. **Never force-push.** Keep the `-presquash` backup. **Do not
  push** — the owner pushes.
- Commit and PR titles start with a JIRA number.
- Full solution green on **EF8, EF9 and EF10**. **Build the three configurations SEQUENTIALLY** (~7 s each) and
  only then run tests in parallel — building them in parallel races on the per-project
  `obj/project.assets.json` and produces bogus `CS0104`/`CS0115` errors that read as source defects. This cost
  the previous slice a cycle.
- **Launch every long `dotnet build`/`dotnet test` from a detached `nohup`'d script and poll the log yourself in
  a loop.** A run started via the sandbox's `run_in_background` dies if an unrelated foreground bash call times
  out and is auto-backgrounded. **Never pipe a test run through `head`/`tail`.**
- Both `MONGODB_URI` and `ATLAS_URI` **unset**. Back-to-back container churn on this host produces spurious
  `System.TimeoutException` failures in `VectorSearch` classes; re-run those in isolation before believing them.
- **Rebuild before every measurement run**, including after reverting a mutation.
- **Zero `#if` lines added or removed in `.cs` under `src/`.** Check new files directly.
- **Preserve each file's BOM state.** `src/.../Query/NativeTranslation/` and its `Stages/` have **no BOM**;
  `tests/.../FunctionalTests/Query/` files **do**. Check a sibling: `head -c 3 <file> | xxd -p` (`2f2a20` = no
  BOM, `efbbbf` = BOM).
- Every guard test **mutation-verified**, both directions, with red counts recorded.
- **Assert VALUES and ORDER, never absence-of-throw and never a row count.** A wrong sort key returns the right
  rows in the wrong order; a wrong constant returns the wrong rows with the right count.
- **Nativeness is proven only by a `MongoQueryMode.NativeOnly` run that succeeds**; a decline only by one that
  throws `NativeTranslationNotSupportedException`. MQL shape proves neither.
- **Measure spec movement by MESSAGE TRANSITION, not by the pass count.** Slice B's count was byte-identical
  before and after while 12 cases converted.
- Tag every documented claim **MEASURED / CITED / INFERRED / UNVERIFIED**. **Re-sum every prose count from the
  table beside it.**
- Breaking changes measured by **executing against the published packages** (`v10.0.2` / `v9.1.2` / `v8.4.2`).
- Each subagent uses its **own uniquely-named scratchpad subdirectory** under
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/5114cfe3-e5e3-4b95-8967-8b81ee667ef9/scratchpad/`.
  Remove any worktree you create; `.claude/worktrees/agent-*` belong to other sessions.
- **There is an unrelated uncommitted one-character whitespace edit** to
  `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`. It is the owner's. Leave it
  uncommitted and out of every commit — use explicit paths with `git add`, **never `git add -A`**.
- **Baseline (EF10) at `4f77e892`, MEASURED:** default `Native` **4593 / 0 / 17**;
  `MONGODB_EF_NATIVE_ONLY=1` **2473 / 2120 / 17**.

### The owner ruling this plan is built on

**For a narrowing cast compared against a constant (`(int)x.D > 0`), native returns the CLR-correct answer and
DOCUMENTS the divergence from driver-LINQ.** MEASURED (spike §3.3): the driver drops the cast and returns
`a,b,c,e` where C# gives `a,b,c`. A natural native implementation emits `{$expr: {$gt: [{$toInt: "$D"}, 0]}}`
and returns `a,b,c`. **This is a result change under the default mode for a shape that currently falls back**,
and it is the first place on this branch where native is deliberately *more correct* than driver-LINQ rather
than merely different. It must be recorded as such in the as-built note — including that it weakens
"results are unchanged" as a blanket argument for the native default, and that `UseQueryMode(DriverLinq)` no
longer restores the same answer for this shape.

### The one ordering constraint that is a correctness requirement

**Task 5 must not ship without its constant rule.** MEASURED (spike §6.2): absorbing a widening cast makes the
comparison happen in the cast's type while the constant is still serialized through the *stored property's*
serializer, so `Where(x => (double)x.I >= 2.5)` emitted `{"I": {"$gte": 2}}` and returned `b,c,d,e` where
`b,c,e` is correct — **silent wrong data under the default `Native` mode**. Every other ordering in this plan
is a preference.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/.../Query/Expressions/MongoConvertExpression.cs` | **new** — the `$toX` IR node: `Operand` + target `Type`, plus the static target→operator map that defines the admissible set. |
| `src/.../Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` | `Render` + `CanRender` arms for the new node. **Not** `IsQueryDialectRenderable` — that exclusion is load-bearing. |
| `src/.../Query/NativeTranslation/MongoFieldPrefixRewriter.cs` | one case for the new node. |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.cs` | `TryTranslateField`'s order-aware unwrap; `TranslateOperand`'s `Convert` branch; `AllFieldsDefaultSerialized`'s recursion. |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.Members.cs` | untouched — noted so nobody goes looking. |
| `src/.../Query/NativeTranslation/NativeProjectionBinder.cs` | the projection-leaf gate. |
| `src/.../Query/NativeTranslation/NativeSlotPopulator.cs` | untouched — slice B's fall-through already routes a declined sort key. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs` | the two renderer arms and the admissible-set boundary. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` | the order-aware unwrap and the `TranslateOperand` arm. |
| `tests/.../FunctionalTests/Query/NativeCastTests.cs` | **new** — the sort defect fixture, the comparison breadth, the constant-serialization pins, the enum arm, the projection leaf, and the declines. |
| `src/.../Query/AGENTS.md` · `docs/native-query-status-EF-322.md` | the as-built note and the slice row. |

**Task boundaries.** Task 2 is pure correctness with zero yield and ships first. Task 3 is inert IR. Tasks 4–7
are the yield, each at its own decline site. Task 8 is the wrap-up. A reviewer can reject any one while
accepting its neighbours.

---

### Task 1: File the A1 ticket

**Files:** one JIRA issue in project `EF`. No repository change.

- [ ] **Step 1: Confirm the JIRA MCP tools are reachable**

Call `mcp__jira__jira_get_issue` for `EF-401`. If it errors, the MCP server's token is missing from the session
environment — stop and report. Load the tools with `ToolSearch` (`select:mcp__jira__jira_create_issue,mcp__jira__jira_update_issue,mcp__jira__jira_get_issue,mcp__jira__jira_link_to_epic`) if they are not listed.

- [ ] **Step 2: Create it, then fill it**

`mcp__jira__jira_create_issue` with `project_key: "EF"`, `issue_type: "Task"`, summary
`Native translation: cast / Convert breadth in comparison, sort and projection position`, and
`description: "Placeholder - full description follows in an update."` **The two-call pattern is required** —
this instance stores `create_issue` descriptions raw and only converts Markdown on `update_issue`. Then fill it
with `mcp__jira__jira_update_issue`, using `h2.` headings and `{code:c#}` (never Markdown `##` or triple
backticks; never `#` for numbered lists — use `* *(1)*`).

The description must carry: the MEASURED yield **30 cases (28 before task 7's re-baselining)**, and that the
CITED figure in the stream-1 spike is 72 total / 56 sole-cause which this slice's own A/B measurement does not
reproduce (spike §5.5); that it fixes a **live silent-wrong-order defect** in cast-bearing sort keys, unreleased
so not a break; and the owner ruling that native returns the CLR answer for a narrowing cast vs a constant,
diverging from driver-LINQ. Link to epic `EF-322` with `mcp__jira__jira_link_to_epic`.

- [ ] **Step 3: Read it back**

`mcp__jira__jira_get_issue` and confirm no literal `##`, no triple backticks, no unintended `h1.`. Note that
this tool's read path partially transcodes wiki→Markdown, so uneven emphasis markers in the response are a
display artifact, not stored corruption — check the `update_issue` echo if in doubt.

- [ ] **Step 4: Record the key**

No repository change. Report the key; every later task's commit message uses it.

---

### Task 2: Fix the silent-wrong-order defect in cast-bearing sort keys

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TryTranslateField`, plus a new private helper)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCastTests.cs`

**Interfaces:**
- Consumes: nothing. **Ships first, before any breadth task.**
- Produces: `TryTranslateField` declines a sort key whose cast is not order-preserving, so slice B's
  `TryTranslateValue` fall-through handles it. Task 3 is what later turns that decline into a rendered `$toInt`.

**The defect, MEASURED (spike §4.1), on a fixture that discriminates (`D` = 1.6, 1.4, 2.5, −1.5, 0.5):**

| sort key | in-memory LINQ | DriverLinq | **Native** | |
|---|---|---|---|---|
| `(int)x.D` | `d,e,a,b,c` | `d,e,a,b,c` | **`d,e,b,a,c`** | native alone disagrees |
| `(uint)x.I` | `d,c,e,b,a` | `ExpressionNotSupported` | **`a,d,c,e,b`** | a genuine order **REVERSAL** |
| `(short)x.I` | `a,d,c,b,e` | `ExpressionNotSupported` | **`a,d,c,e,b`** | a genuine order **REVERSAL** |

Cause: `TryTranslateField` calls `Unwrap`, which strips **any** `Convert`/`ConvertChecked` unconditionally, so
`$sort` orders by the **raw stored value**. It does **not** predate the native work — `Unwrap` and
`TryTranslateField` arrived in the same commit (`1d5580e3`, EF-323) and `Infrastructure/MongoQueryMode.cs` does
not exist at `v10.0.2`/`v9.1.2`/`v8.4.2`, so the whole native path is **unreleased**: no `BREAKING-CHANGES.md`
entry.

- [ ] **Step 1: Create the branch and write the failing functional test**

```bash
cd /Users/arthur.vickers/code/mongo-efcore-provider
git checkout NativeQueryOngoing && git pull --ff-only
git checkout -b EF-<key>
head -c 3 tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeBareProjectionTests.cs | xxd -p
```
Expect `efbbbf` — the new functional file needs a **BOM**.

Create `NativeCastTests.cs` following `NativeBareProjectionTests` for structure: `[XUnitCollection("QueryTests")]`,
`IClassFixture<TemporaryDatabaseFixture>`, and a private `CreateContext(IMongoCollection<T>, MongoQueryMode)`
built via `SingleEntityDbContext.Create` with `new MongoDbContextOptionsBuilder(b).UseQueryMode(mode)` and
`b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))`.

**Seed the discriminating fixture and state its orders in a comment** — the whole test rests on them:

```csharp
// Rows a..e. D is the double that makes truncation observable; I is the int that makes the
// signed/unsigned and narrowing reinterpretations observable.
//   a: D =  1.6, I =        1
//   b: D =  1.4, I =        2
//   c: D =  2.5, I =        3
//   d: D = -1.5, I = -1          // negative: (uint) reinterprets it as huge, (short) preserves it
//   e: D =  0.5, I =    70000    // > short.MaxValue: (short) wraps NEGATIVE
//
// raw-D order:      d, e, b, a, c        <- what native WRONGLY sorts by today for (int)x.D
// (int)D order:     d, e, a, b, c        <- 1.6 and 1.4 both truncate to 1 and tie, so a,b keep insertion order
// raw-I order:      d, a, b, c, e
// (uint)I order:    a, b, c, e, d        <- d becomes the largest
// (short)I order:   d, a, b, c, e ... with e NEGATIVE, so: e, d, a, b, c
```

Compute the last three orders yourself from the seeded values and assert what you compute — do not copy these
comments blindly; if your arithmetic disagrees with them, your arithmetic wins and the comment is wrong.

Cases:

1. `Narrowing_cast_sort_key_no_longer_sorts_by_the_raw_value` — `OrderBy(x => (int)x.D)` under default
   `Native`, asserting the `(int)D` order **and** `Assert.NotEqual` against the raw-D order. Compare against
   **both** oracles in the same test: explicit `DriverLinq` and an in-memory LINQ evaluation over the same
   expression. This is the defect's pin.
2. `Unsigned_reinterpreting_cast_sort_key_declines` — `OrderBy(x => (uint)x.I)`: throws under `NativeOnly`.
   Under default `Native` it falls back, and the driver refuses the shape too, so assert the failure is **loud**
   (an exception, not a silently different order) and record its type.
3. `Narrowing_integral_cast_sort_key_declines` — `OrderBy(x => (short)x.I)`, same shape as 2.
4. `Widening_cast_sort_keys_are_unchanged_and_native` — `(double)x.I`, `(long)x.I` and the boxing
   `(object)x.D` all still go native under `NativeOnly` and return the raw order (they are order-preserving).
   This is the control that stops the fix over-declining.

- [ ] **Step 2: Run it and confirm case 1 fails on ORDER**

```bash
SCRATCH=<your scratchpad>; mkdir -p $SCRATCH
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeCastTests" > $SCRATCH/f.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/f.log
```
Expected: case 1 fails with the raw-D order where the `(int)D` order is expected — **the defect, reproduced**.
Cases 2 and 3 currently return a silently wrong order rather than throwing, so they fail too. Case 4 passes.

- [ ] **Step 3: Implement the order-aware unwrap**

In `MongoExpressionTranslator.cs`, change `TryTranslateField` to use a new helper instead of `Unwrap`:

```csharp
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path))
            return false;
```

and add the helper beside `Unwrap`:

```csharp
    /// <summary>
    /// Strips only the <see cref="ExpressionType.Convert"/> layers that PRESERVE ORDER, for a sort key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a sort key cannot use the general <see cref="Unwrap"/>.</b> That helper strips any
    /// <c>Convert</c>/<c>ConvertChecked</c> unconditionally, so a narrowing or signed/unsigned cast in a sort
    /// key was silently discarded and <c>$sort</c> ordered by the RAW STORED VALUE. MEASURED:
    /// <c>OrderBy(x =&gt; (int)x.D)</c> over a <c>double</c> returned a different order from BOTH in-memory
    /// LINQ and explicit <c>DriverLinq</c>, and <c>(uint)x.I</c> / <c>(short)x.I</c> were genuine order
    /// REVERSALS — wrong rows in the wrong order, with no exception, under the default mode.
    /// </para>
    /// <para>
    /// <b>What is order-preserving, and why each one is.</b> A nullable ↔ underlying convert (which EF inserts
    /// freely) changes no value; a boxing convert to <see cref="object"/> changes no value; and a WIDENING
    /// numeric convert is monotonic by definition — <see cref="WideningNumericConversions"/> is exactly the
    /// C# implicit-conversion table, every entry of which is value-preserving. Everything else stays in the
    /// tree, so <see cref="TryResolveMember"/> declines it and the caller falls through to
    /// <see cref="TryTranslateValue"/>, which can render an explicit <c>$toX</c> (or decline in turn).
    /// </para>
    /// <para>
    /// <b>Do not "simplify" this back to <see cref="Unwrap"/>.</b> Doing so reintroduces the defect above; the
    /// functional pins in <c>NativeCastTests</c> are what catch it.
    /// </para>
    /// </remarks>
    private static Expression UnwrapOrderPreserving(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var fromType = Nullable.GetUnderlyingType(u.Operand.Type) ?? u.Operand.Type;
            var toType = Nullable.GetUnderlyingType(u.Type) ?? u.Type;

            if (fromType != toType
                && toType != typeof(object)
                && !IsWideningNumericConvert(fromType, toType))
            {
                return e; // order-changing: leave it in place so the caller declines and falls through
            }

            e = u.Operand;
        }

        return e;
    }
```

- [ ] **Step 4: Add the unit tests**

In `MongoExpressionTranslatorTests`, using the existing `GetEntityType<T>` / `NewTranslator` / `FieldBody<T>`
helpers and a fixture with a `double D` and an `int I`:

```csharp
[Fact]
public void Sort_key_keeps_a_narrowing_cast_so_it_declines_rather_than_sorting_by_the_raw_value()
{
    var translator = NewTranslator(GetEntityType<Customer>());

    // (int)c.DoubleScore — order-CHANGING, so TryTranslateField must NOT resolve it to the raw field.
    Assert.False(translator.TryTranslateField(FieldBody<Customer>(c => (int)c.DoubleScore), out _));
}

[Fact]
public void Sort_key_still_strips_an_order_preserving_cast()
{
    var translator = NewTranslator(GetEntityType<Customer>());

    // Widening, boxing and nullable converts are all value-preserving and must still resolve to the field.
    Assert.True(translator.TryTranslateField(FieldBody<Customer>(c => (double)c.Age), out var widened));
    Assert.Equal("Age", widened!.ElementName);

    Assert.True(translator.TryTranslateField(FieldBody<Customer>(c => (object)c.Age), out var boxed));
    Assert.Equal("Age", boxed!.ElementName);
}
```

- [ ] **Step 5: Run both suites**

Unit and functional, as in Step 2. Expected: the two unit tests pass; functional cases 1–4 all pass — case 1
now returns the `(int)D` order matching both oracles, and cases 2 and 3 now fail **loudly** instead of returning
a wrong order.

- [ ] **Step 6: Mutation-verify**

Restore the blanket `Unwrap` in `TryTranslateField`, rebuild, run the functional class, record the red count.
Expect cases 1–3 red. Revert, **rebuild**, re-run. If nothing goes red the fixture does not discriminate — fix
the fixture, not the count.

- [ ] **Step 7: Confirm zero spec movement, on both axes**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/spec-native.log 2>&1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=a1-task2.trx" --results-directory $SCRATCH > $SCRATCH/spec-no.log 2>&1
grep -E "Passed!|Failed!|Failed:" $SCRATCH/spec-native.log $SCRATCH/spec-no.log
```
Expected: **4593 / 0 / 17** and **2473 / 2120 / 17**, unmoved (spike §4.4 measured this fix's spec cost as
zero on both axes). **Keep `a1-task2.trx`** — later tasks compare against it.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCastTests.cs
git commit -m "EF-<key>: stop a cast-bearing sort key ordering by the raw stored value"
```

---

### Task 3: `MongoConvertExpression` — the `$toX` node

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConvertExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateOperand`, `AllFieldsDefaultSerialized`)
- Modify: the two unit-test files above

**Interfaces:**
- Consumes: Task 2.
- Produces: `MongoConvertExpression(MongoExpression operand, Type clrType)` with `Operand` and `Type`, and
  `MongoConvertExpression.ToOperatorFor(Type) → string?` returning `"$toInt"`, `"$toLong"`, `"$toDouble"`,
  `"$toDecimal"` or `null`. **Tasks 4–7 depend on `ToOperatorFor` as the single definition of the admissible
  set.**

**The admissible set is bounded by MQL, not by taste (spike §3.2, MEASURED).** For a target of `short`, `uint`
or `float` **the driver's own LINQ provider throws `ExpressionNotSupportedException`** in predicate, sort and
projection position alike, because MQL has no `$toShort`/`$toUInt`/`$toFloat`. Declining those is the same
boundary the oracle has, not a gap A1 chose.

**Yield: 0 specification cases** (spike §5). This task exists because Task 2 sorts by it and Task 7 projects it.

- [ ] **Step 1: Write the failing renderer unit tests**

In `MongoAggregationExpressionRendererTests` (follow its existing idiom — it renders a node and asserts a
`BsonDocument`/`BsonValue`):

```csharp
[Theory]
[InlineData(typeof(int), "$toInt")]
[InlineData(typeof(long), "$toLong")]
[InlineData(typeof(double), "$toDouble")]
[InlineData(typeof(decimal), "$toDecimal")]
public void Convert_node_renders_the_matching_to_operator(Type target, string op)
{
    var node = new MongoConvertExpression(new MongoElementRefExpression("D", typeof(double)), target);

    var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

    Assert.Equal(BsonDocument.Parse($$"""{ "{{op}}" : "$D" }"""), rendered.AsBsonDocument);
}

[Theory]
[InlineData(typeof(short))]
[InlineData(typeof(uint))]
[InlineData(typeof(float))]
public void Convert_node_to_a_target_MQL_cannot_express_is_not_renderable(Type target)
{
    // MQL has no $toShort/$toUInt/$toFloat, and the driver's own LINQ provider throws for these targets too —
    // so declining is the same boundary the oracle has, not a coverage choice.
    Assert.Null(MongoConvertExpression.ToOperatorFor(target));
    Assert.False(MongoAggregationExpressionRenderer.CanRender(
        new MongoConvertExpression(new MongoElementRefExpression("I", typeof(int)), target)));
}

[Fact]
public void Convert_node_is_NOT_query_dialect_renderable()
{
    // LOAD-BEARING: $expr is a hard server error inside $elemMatch, so a node that only the aggregation
    // dialect can express must never be admitted by the query-dialect classifier.
    Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
        new MongoConvertExpression(new MongoElementRefExpression("D", typeof(double)), typeof(int))));
}

[Fact]
public void Convert_node_reports_unrenderable_when_its_OPERAND_is()
{
    // MongoUnaryExpression is one of the node kinds the aggregation dialect cannot express (CanRender admits
    // field/element refs, constants/parameters, binaries over its listed operators, and the two size nodes —
    // nothing else). Wrapping it in a convert must not launder it into renderability.
    var unrenderable = new MongoUnaryExpression(
        MongoUnaryOperator.Not, new MongoElementRefExpression("Flag", typeof(bool)));

    Assert.False(MongoAggregationExpressionRenderer.CanRender(
        new MongoConvertExpression(unrenderable, typeof(int))));
}
```

- [ ] **Step 2: Run them and confirm they fail to compile**

The node does not exist yet — that is the failure. Record it.

- [ ] **Step 3: Create the node**

`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConvertExpression.cs`, licence header copied
byte-for-byte from a sibling in that directory, **matching that sibling's BOM state**:

```csharp
using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An explicit type conversion of <see cref="Operand"/> to <see cref="Type"/>, rendered in the
/// aggregation-expression dialect as one of MQL's four <c>$to…</c> operators.
/// </summary>
/// <remarks>
/// <para>
/// EF-322 stream 1, slice A1. Built for a cast the translator must NOT simply unwrap — a narrowing or
/// signed/unsigned conversion changes the value, so dropping it silently changes results (a sort key ordered
/// by the raw stored value; a comparison evaluated in the wrong type).
/// </para>
/// <para>
/// <b>The admissible set is bounded by MQL itself, not by taste.</b> <see cref="ToOperatorFor"/> maps only
/// <see cref="int"/>, <see cref="long"/>, <see cref="double"/> and <see cref="decimal"/>; there is no
/// <c>$toShort</c>, <c>$toUInt</c> or <c>$toFloat</c>. MEASURED: the driver's own LINQ provider throws
/// <c>ExpressionNotSupportedException</c> for those targets in predicate, sort and projection position alike,
/// so declining them keeps native and the fallback at the SAME boundary.
/// </para>
/// <para>
/// <b>This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.</b>
/// It has no query-dialect form, and <c>$expr</c> is a hard server error inside <c>$elemMatch</c> — so
/// admitting it there would turn a clean decline into a runtime failure. See that classifier's own remarks.
/// </para>
/// </remarks>
internal sealed class MongoConvertExpression(MongoExpression operand, Type clrType) : MongoExpression
{
    /// <summary>The expression whose value is converted.</summary>
    public MongoExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;

    /// <summary>
    /// The MQL conversion operator for <paramref name="clrType"/>, or <see langword="null"/> when MQL cannot
    /// express it. This is the single definition of the admissible set — every gate consults it rather than
    /// re-deriving one.
    /// </summary>
    public static string? ToOperatorFor(Type clrType)
    {
        var target = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return target == typeof(int) ? "$toInt"
            : target == typeof(long) ? "$toLong"
            : target == typeof(double) ? "$toDouble"
            : target == typeof(decimal) ? "$toDecimal"
            : null;
    }
}
```

- [ ] **Step 4: Add the renderer arms**

In `MongoAggregationExpressionRenderer.Render`'s switch, beside the existing arms:

```csharp
            MongoConvertExpression convert
                => new BsonDocument(
                    MongoConvertExpression.ToOperatorFor(convert.Type)
                        ?? throw new NativeTranslationNotSupportedException(
                            $"MQL has no conversion operator for '{convert.Type.Name}'. A convert to an "
                            + "unrenderable target should have been declined at translate time."),
                    Render(convert.Operand, placeholders, elementVariable)),
```

and in `CanRender`:

```csharp
            MongoConvertExpression convert
                => MongoConvertExpression.ToOperatorFor(convert.Type) is not null && CanRender(convert.Operand),
```

**Do not touch `MongoQueryLanguageRenderer.IsQueryDialectRenderable`** — the exclusion is the point.

- [ ] **Step 5: Add the prefix-rewriter and serialization-guard cases**

`MongoFieldPrefixRewriter`'s switch gains:

```csharp
            MongoConvertExpression c => new MongoConvertExpression(Rewrite(c.Operand, prefix), c.Type),
```

and `MongoExpressionTranslator.AllFieldsDefaultSerialized` gains a recursion so a converted operand is still
checked:

```csharp
            MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand),
```

Both are easy to forget and neither is exercised by the spec suite; Step 8's tests are their only net.

- [ ] **Step 6: Admit a renderable narrowing cast in `TranslateOperand`**

Replace the `Convert` branch's unconditional `return null` for a type-changing cast:

```csharp
        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            var fromType = Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type;
            var toType = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
            if (fromType != toType && !(allowNumericWidening && IsWideningNumericConvert(fromType, toType)))
            {
                // A type-changing cast MQL can express becomes an explicit $toX over the translated operand —
                // which is what the driver's own LINQ provider emits in this position (MEASURED, spike §3.1).
                // An unrenderable target still declines, matching the driver, which throws for those.
                if (MongoConvertExpression.ToOperatorFor(toType) is null)
                    return null;

                var converted = TranslateOperand(unary.Operand, allowNumericWidening);
                return converted is null ? null : new MongoConvertExpression(converted, toType);
            }

            return TranslateOperand(unary.Operand, allowNumericWidening); // benign or widening — unwrap
        }
```

- [ ] **Step 7: Run the unit tests**

Expected: all of Step 1's pass, and the rest of both unit classes unmoved.

- [ ] **Step 8: Add the functional breadth cases and confirm the sort defect now CONVERTS**

Add to `NativeCastTests`:

5. `Narrowing_cast_sort_key_now_goes_native` — `OrderBy(x => (int)x.D)` under **`NativeOnly`** now succeeds
   (Task 2 made it decline; this task makes it convert), returning the `(int)D` order, and its captured MQL
   contains `{"$toInt": "$D"}` inside a `$set`. **Caption the MQL assertion as a stage-shape pin, not a routing
   proof.**
6. `Field_to_field_comparison_with_a_cast_goes_native` — the spike's §3.1 shapes under `NativeOnly`, each
   asserting `Native == DriverLinq == CLR`.
7. `Cast_to_an_unrenderable_target_still_declines` — `(short)`/`(uint)`/`(float)` in a comparison: throws under
   `NativeOnly`, and the driver refuses it too, so the fallback also fails loudly.
8. `Cast_inside_a_quantifier_element_predicate_declines` — a cast inside `Any(...)`'s element predicate, which
   must decline rather than reach `$elemMatch`. **This is the pin for the `IsQueryDialectRenderable` exclusion**
   — mutation-verify it by adding the node to that classifier and confirming this test goes red.

- [ ] **Step 9: Mutation-verify the two guards**

1. Add `MongoConvertExpression` to `IsQueryDialectRenderable`. Rebuild, run, record — case 8 must go red.
   Revert, **rebuild**, re-run.
2. Delete the `MongoFieldPrefixRewriter` case. Rebuild, run, record. If nothing goes red, **say so** and add a
   test that reaches the rewriter with a converted operand (an owned `SelectMany` inner filter is the usual
   route) rather than recording the case as covered.

- [ ] **Step 10: Confirm zero spec movement and commit**

Both axes against `a1-task2.trx`, expecting no transitions (spike §5 measured this arm at 0). Then commit with
explicit paths.

---

### Task 4: The widening relaxation at `TranslateComparison` — **with its constant rule**

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateComparison`, `HasNumericConvert`)
- Modify: `tests/.../FunctionalTests/Query/NativeCastTests.cs`

**Interfaces:** consumes Tasks 2–3. Produces the slice's largest yield.

**Yield: 18 specification cases, MEASURED (spike §5.2), needing NO re-baselining.**

**This is the task the whole plan is sequenced around, and it must not ship without its constant rule.**
`TranslateComparison` serializes the constant with `forSerialization: leftProperty` — the **stored** type. Once
a widening cast is absorbed the comparison happens in the **cast's** type, and a fractional constant is
truncated to the integral stored type. MEASURED with the prototype live:

| shape | in-memory LINQ | DriverLinq | prototype without the rule | emitted |
|---|---|---|---|---|
| `(double)x.I >= 2.5` | `b,c,e` | `b,c,e` | **`b,c,d,e`** | `{"I": {"$gte": 2}}` |
| `(decimal)x.I >= 2.5m` | `b,c,e` | `b,c,e` | **`b,c,d,e`** | `{"I": {"$gte": 2}}` |

**And a blanket fix is itself wrong** — serializing every convert layer's constant in the comparison type cost
5 specification and 23 functional failures, all enum-as-string or value-converted properties, because the
enum/identity arm *requires* the property serializer. **The rule is conditional:** serialize the constant in the
**comparison's type** for a **numeric widening** cast over a **default-serialized** property; keep the
**property's** serializer otherwise.

- [ ] **Step 1: Write the failing functional pins**

Add to `NativeCastTests`, over a fixture with an `int I` seeded so a fractional threshold discriminates:

9. `Widening_cast_comparison_with_a_fractional_constant_returns_the_right_rows` —
   `Where(x => (double)x.I >= 2.5)` under `NativeOnly`, asserting the **rows**, plus `Assert.NotEqual` against
   the truncated-constant row set. Assert the same for `(decimal)x.I >= 2.5m`. **This is the silent-wrong-data
   pin; it must fail loudly if the constant is serialized through the stored property.**
10. `Widening_cast_comparison_emits_the_constant_in_the_comparison_type` — captured MQL shows
    `{"I": {"$gte": 2.5}}`, byte-identical to what the driver emits. Stage-shape pin, not a routing proof.
11. `Widening_cast_comparison_matches_driver_linq` — `Native == DriverLinq` for the same shapes.

- [ ] **Step 2: Run and confirm they fail**

Cases 9–11 fail: today the whole shape declines (`NativeOnly` throws), so they fail on the decline, not yet on
the constant. Record that distinction — after Step 3 they must fail on the *constant* if the rule is missing,
which is what Step 5's mutation proves.

- [ ] **Step 3: Relax `HasNumericConvert` and apply the conditional constant rule**

`HasNumericConvert` currently reports "the member side wraps a type-changing numeric convert", and
`TranslateComparison` returns `null` on it. Change it to classify instead of veto — keep the method name and
add an `out` for the tolerated target type, or introduce a small private helper beside it; either way:

- a **widening numeric** convert over the member ⇒ **tolerated**, and the comparison type is the cast's type;
- anything else ⇒ unchanged, still `return null`.

Then, at both `TranslateValue(rightUnwrapped, leftProperty)` call sites, choose the serialization context:

```csharp
            // The constant must be serialized in the type the comparison actually happens in. Absorbing a
            // widening cast moves that from the STORED type to the CAST's type, and a fractional constant
            // truncated to an integral stored type returns WRONG ROWS, silently, under the default mode
            // (MEASURED: (double)x.I >= 2.5 emitted {"I": {"$gte": 2}} and returned b,c,d,e where b,c,e is
            // correct). The condition is NOT optional and NOT blanket: applied to every convert layer it broke
            // 5 spec and 23 functional cases, all enum-as-string or value-converted, because the enum/identity
            // arm REQUIRES the property serializer to render its constant.
            var valueExpr = toleratedWideningTarget is not null
                            && NativeGroupByBinder.HasDefaultKeySerialization(leftProperty)
                ? TranslateValue(rightUnwrapped, forSerialization: null)
                : TranslateValue(rightUnwrapped, leftProperty);
```

**UNVERIFIED, and say so in the comment:** whether `HasDefaultKeySerialization` is exactly the right conjunct
(versus "the property's CLR type is not an enum") was not separated by any fixture in the spike. If your own
tests can separate them, do, and record which you used.

- [ ] **Step 4: Run the functional pins**

Expected: cases 9–11 pass, and cases 1–8 unmoved.

- [ ] **Step 5: Mutation-verify the constant rule specifically**

Change the ternary to always pass `leftProperty`. Rebuild, run, record — **case 9 must go red on ROWS**, not on
routing. Revert, **rebuild**, re-run. Then change it to always pass `null` and run the **whole functional
suite plus the EF10 spec suite**: the spike measured that blanket form at 5 spec + 23 functional failures, so a
much smaller number means your condition is not reaching the enum arm and the two halves are not both pinned.
Record both counts.

- [ ] **Step 6: Measure the spec delta on both axes**

Against `a1-task2.trx`, by **message transition**. **Expected: 18 `Failed→Passed`, 0 `Passed→Failed`, and NO
`AssertMql` re-bases** (spike §5.2 measured this arm as needing none). A materially different number is a
finding to report with the new first-decline sites, not a reason to widen the arm.

- [ ] **Step 7: Commit**

---

### Task 5: The enum / `char` / boxing arm

**Files:** as Task 4, plus `tests/.../FunctionalTests/Query/NativeGateRoutingTests.cs`

**Yield: 10 specification cases, MEASURED.**

Admit the identity-like converts at the same site: an enum to its own underlying type, `char` → `int`, and a
boxing convert to `object`. **Keep the PROPERTY serializer for the constant** on this arm (spike §6.2) — that is
how an enum-as-string constant renders.

- [ ] **Step 1: Write the failing pins**

12. `Enum_as_string_comparison_goes_native_and_returns_the_right_values` — an enum-as-string fixture,
    asserting **VALUES** in all three modes, not just routing. The spike's §5.3 notes this arm flips
    `NativeGateRoutingTests.A_enum_as_string_where_equals_routing` (whose comment locks the fallback by name)
    while its parity sibling stayed green — so values were correct; assert them here anyway.
13. `Char_and_boxing_converts_go_native` — the same, for `char` → `int` and a boxing convert.

- [ ] **Step 2: Run and confirm they fail** (the shape declines today).

- [ ] **Step 3: Implement the arm**

Extend Task 4's classification rather than adding a second site. Three converts join the tolerated set, and
all three are **identity-like** — they do not change the stored value's ordering or equality, only its declared
CLR type:

```csharp
    // Identity-like converts: the comparison happens on the SAME stored value, so the member side needs no
    // $toX and the constant KEEPS the property's serializer (that is how an enum-as-string constant renders
    // at all — see the constant rule's second half).
    //   enum   -> its own Enum.GetUnderlyingType, and back
    //   char   -> int
    //   T      -> object (boxing)
    private static bool IsIdentityLikeConvert(Type fromType, Type toType)
        => (fromType.IsEnum && toType == Enum.GetUnderlyingType(fromType))
            || (toType.IsEnum && fromType == Enum.GetUnderlyingType(toType))
            || (fromType == typeof(char) && toType == typeof(int))
            || toType == typeof(object);
```

The classification therefore has three outcomes, not two: **widening numeric** (tolerate, constant in the
comparison type), **identity-like** (tolerate, constant keeps the property serializer), **anything else**
(unchanged — `return null`). Keep those three arms visibly distinct in the code; collapsing the first two is
exactly the blanket rule Task 4 measured to be wrong.

- [ ] **Step 4: Deliberately re-baseline the locked routing pin**

`NativeGateRoutingTests.A_enum_as_string_where_equals_routing` asserts the fallback and its comment locks it by
name. Flip it to assert native routing, **rewrite the comment to say why the lock was lifted**, and confirm its
parity sibling `A_enum_as_string_where_equals_parity` stays green. Do not delete either.

- [ ] **Step 5: Run, mutation-verify, measure both axes**

Expected: **10 `Failed→Passed`, 0 `Passed→Failed`**. Mutation: force the enum arm to decline and confirm cases
12–13 go red.

- [ ] **Step 6: Commit**

---

### Task 6: The projection-leaf gate

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Modify: `tests/.../FunctionalTests/Query/NativeCastTests.cs`

**Yield: 0 specification cases, MEASURED** — this task is breadth for functional shapes, not spec movement.

`TryTranslateLeaf` pre-filters on `leafExpression is MemberExpression` (or an `EF.Property` call, added by slice
A2), so a `UnaryExpression{Convert}` leaf never reaches the translator. Widen it, **gated on the resulting node
kind being `MongoConvertExpression`** — the same "it renders as a DOCUMENT, so `$project` cannot misread it as
an inclusion flag" argument the count and arithmetic branches already use.

**Read the A2 lesson before starting:** widening this exact gate for `EF.Property` exposed a silent-wrong-data
defect in `MongoProjectionBindingExpressionVisitor.IsScalarMethodPropertyAccess`, which pattern-matched the
receiver without unwrapping a `Convert`. **A cast leaf is precisely a `Convert`.** Re-examine that method and
the DOM read-back path for this leaf shape before assuming they handle it, and say in your report what you
checked.

- [ ] **Step 1: Write the failing pins** — a wrapped cast leaf (`new { X = (int)x.D }`) and a bare one
      (`Select(x => (int)x.D)`) under `NativeOnly`, asserting VALUES; plus a **parameterized-`Where` leg** for
      each (a captured local in `string.StartsWith`, run under default `Native`), because the bare spelling's
      alias route is where an alias miss is silent.
- [ ] **Step 2: Run and confirm they fail** (`NativeOnly` throws today for both spellings).
- [ ] **Step 3: Widen the gate**, gated on the node kind.
- [ ] **Step 4: Run, and re-check the shaper read-back** per the A2 lesson above.
- [ ] **Step 5: Mutation-verify** the node-kind gate: relax it to plain translation success and confirm a
      constant leaf is admitted (which it must not be) — the same discrimination A2's equivalent test uses.
- [ ] **Step 6: Confirm zero spec movement on both axes, then commit.**

---

### Task 7: The site-B fall-through — and the CLR-vs-driver divergence

**Files:** as Task 4, plus two specification baselines.

**Yield: 2 specification cases, after re-baselining.**

Today `TranslateComparison`'s query-native branch returns `null` when the member side carries a cast it cannot
absorb. Let it **fall through** to the general `$expr` path instead, where Task 3's `MongoConvertExpression`
renders it.

**This is where the owner's ruling lands.** For a narrowing cast against a constant, the driver DROPS the cast
and returns a CLR-wrong answer (`(int)x.D > 0` → `a,b,c,e` where C# gives `a,b,c`); native will emit
`{$expr: {$gt: [{$toInt: "$D"}, 0]}}` and return `a,b,c`. **The owner has ruled: take the CLR-correct answer and
document the divergence.**

- [ ] **Step 1: Write the pin that states the divergence explicitly**

14. `Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq` — assert **all three**
    legs: native returns the CLR rows; in-memory LINQ agrees; explicit `DriverLinq` returns its own different
    rows. The test's name and comment must say this divergence is **deliberate and owner-ruled**, so nobody
    "fixes" it later. This is the opposite of the EF-359 family (where native and driver agree and both differ
    from the CLR) and the note must say so.

- [ ] **Step 2: Implement the fall-through.**
- [ ] **Step 3: Re-baseline exactly 2 cases**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/rebase.log 2>&1
git diff --stat tests/MongoDB.EntityFrameworkCore.SpecificationTests/
```
Expected: `Decimal_cast_to_double_works` × 2 async, and **nothing else**. Read each rewritten baseline and
confirm it is the `$expr`/`$toX` shape you intended. If any other baseline moved, stop and report.

- [ ] **Step 4: Measure both axes by message transition** — expecting 2 `Failed→Passed`, 0 `Passed→Failed`.
- [ ] **Step 5: Commit.**

---

### Task 8: Tripwires, sweep, record, squash

**Files:** `tests/.../FunctionalTests/Query/NativeExprComparisonTests.cs`, `src/.../Query/AGENTS.md`,
`docs/native-query-status-EF-322.md`

- [ ] **Step 1: Re-baseline the two decline tripwires as capability tests**

`NativeExprComparisonTests.NativeOnly_cast_in_field_to_field_comparison_throws` and
`..._cast_in_arithmetic_operand_throws` pin today's decline and are **meant** to be flipped deliberately. Turn
each into a capability test asserting the shape now goes native and returns correct values, and rewrite the
comment to record what changed and when.

- [ ] **Step 2: Whole solution on all three EF versions** — build sequentially, then test. 0 failures each.

- [ ] **Step 3: Both spec axes, final**

Expected: default `Native` **4593 / 0 / 17**; `MONGODB_EF_NATIVE_ONLY=1` **2473 + 30 = 2503 / 2120 − 30 = 2090 /
17**. **Re-derive both from the baseline plus your own measured transition count** rather than trusting this
line, and report the transition sets. `Passed→Failed` must be empty.

- [ ] **Step 4: Break check against the release tags**

Every touched type is `internal` and `MongoQueryMode.cs` does not exist at `v10.0.2`/`v9.1.2`/`v8.4.2`, so the
native path is unreleased and the sort-defect fix cannot be a break. **The one thing to check by execution** is
Task 7's divergence: confirm that at the published versions the shape ran through driver-LINQ (it did — there
was no other path), so nothing that returned a value before returns a different one *at a released version*.
State the conclusion and how you reached it.

- [ ] **Step 5: Write the as-built note**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, carrying:
- the **live defect** this slice fixed, with the three measured orders, that it was unreleased, and that
  `UnwrapOrderPreserving` must not be simplified back to `Unwrap`;
- the **admissible set** and that it is MQL's boundary, not a choice — the driver throws for
  `short`/`uint`/`float` too;
- that `MongoConvertExpression` is deliberately **excluded** from `IsQueryDialectRenderable`, and why
  (`$expr` inside `$elemMatch` is a hard server error);
- the **conditional constant rule** in full, both halves measured, with the blanket version's 5 + 23 failures
  recorded so nobody applies it globally;
- the **owner-ruled divergence** of Task 7, stated as deliberate, with the contrast to the EF-359 family and
  the note that `UseQueryMode(DriverLinq)` no longer restores the same answer for that shape;
- the measured yield — **30 (28 before re-baselining)** — and that the CITED 72/56 is **not reproduced**;
  the spike's §5.5 characterises the gap rather than itemising it, and this note must not restate 72/56 as
  though it were delivered;
- that the **sort and projection columns delivered 0 spec cases each**, so they are correctness/breadth work;
- the mutation evidence from every task.

Tag every number, and re-sum every count from the table beside it.

- [ ] **Step 6: Add the status-doc row**, matching §2's existing columns, with the same "not 72/56" caveat.

- [ ] **Step 7: Squash, fast-forward — do NOT push**

```bash
git branch -f EF-<key>-presquash HEAD
git reset --soft $(git merge-base HEAD NativeQueryOngoing)
git commit -F $SCRATCH/msg.txt
git diff --quiet EF-<key>-presquash HEAD && echo "squash content-identical"
git checkout NativeQueryOngoing && git merge --ff-only EF-<key>
```

The message must record the defect fix, the admissible set, the constant rule, the owner-ruled divergence, the
measured yield with its "not 72/56" caveat, and the three-EF-version and both-axis results.

---

## What comes after this plan

**A4** — the reverted tier 2 (28 sole-cause) — is the last of the measured slice-B-independent tranche. It has
a **recorded prerequisite**: the late-fallback path must emit `$ifNull` itself rather than inherit the driver's
bare `$size`. **Do not re-attempt A4 without that fixed** (see the step-3a note in `Query/AGENTS.md`).

After that, the remaining capability-A slices — and two facts from this slice that should shape how they are
planned:

1. **Sole-cause figures have now under-delivered three times** (A2 34/44, A5 0/36, A1 30/56). The decomposition
   spike's classifier stops at the minimal failing subtree, so it cannot see an enclosing blocker — and A1 adds
   a second failure mode: **it can also point at the wrong decline site entirely**. A1's yield lives at
   `TranslateComparison`/`HasNumericConvert`, which no A1 write-up named; a plan written against the site the
   documents pointed at would have delivered 0 of 28. **Size a slice by a prototype A/B, not by the table.**
2. **At least A6 (18) + A13 (18) = 36 of the 92 sort-position cases still need an aggregation-dialect renderer
   arm** that the stream-1 spike's new-node-kind obligation does not cover, because `MongoInExpression` and
   `MongoUnaryExpression` already exist. `MongoConvertExpression` is the worked example of what that arm looks
   like.
