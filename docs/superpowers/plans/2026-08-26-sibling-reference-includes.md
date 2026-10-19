# Sibling Reference Includes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make native single-level reference `Include` admit sibling reference Includes on the same query —
`Lines.Include(l => l.Order).Include(l => l.Product)` (different target types) and
`Docs.Include(d => d.Author).Include(d => d.Editor)` (same target type, `Buyer`) — instead of falling back to
DriverLinq, without changing the "reference + collection" or `ThenInclude` dispositions (both must keep
declining).

**Architecture:** EF's nav-expansion compiles N sibling reference Includes into a chain of nested
`IncludeExpression`s linked via `EntityExpression` (outermost = last `.Include()` call), bottoming out at a
pure `.Outer` member-access chain reaching the trailing `Select`'s own parameter. The join/lookup-registration
machinery (`TranslateJoinCore`/`RebindInnerShaperToOuterQuery` in
`MongoQueryableMethodTranslatingExpressionVisitor.cs`) already handles N joins correctly and unconditionally —
once `Joins.Count > 1` it flattens every join's `LookupExpression` (correct alias, `ForceUnwind`,
`PreserveNullAndEmptyArrays`) into `_pendingLookups`, independent of whether any Include ever confirms. The
gap is entirely in the recognizer: `IsSingleLevelReferenceIncludeSelector`/`TryConfirmReferenceInclude` only
recognize a single, unnested `IncludeExpression` and only confirm one navigation, leaving the
candidate/confirmed counter (`MongoSelectDefinition._candidateReferenceIncludeJoins` /
`_confirmedReferenceIncludes`) permanently unbalanced for 2+ siblings, which is exactly what keeps
`Route == Fallback` today (the SAFE default-deny; there is no wrong-data risk from the current recognizer, only
missed native coverage). This plan replaces the single-navigation recognizer with a chain-walking one that
recognizes N nested levels, validates each navigation with the existing per-navigation guards, and confirms
each one — reusing (not duplicating) the join-registration state that already exists by the time the trailing
`Select` runs.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit functional tests against a real MongoDB
(`TemporaryDatabaseFixture`).

**Spec:** No separate spec doc — this plan's Architecture section, plus the cited file/line evidence below, is
the design record (mirrors the size of change: a two-function replacement plus test flips, not new
architecture).

## Global Constraints

- `<Nullable>enable</Nullable>` — annotate any new nullable-returning helper accordingly.
- No `#if EF8/EF9/EF10` guards expected — this touches only EF-version-independent expression-tree shapes;
  confirm with a full three-version build+test pass in the final task.
- Do not change `MongoSelectLowerer`, `LookupExpression`, or `MongoQueryExpression.Lookup.cs` — the
  investigation (recorded in this plan) confirmed the join/lookup machinery there is already general; touching
  it is out of scope and a red flag if a task believes it's necessary.
- Every decline this ticket did not target (`ThenInclude`/transitive, filtered Include, reference + collection
  combo, composite FK/PK, post-terminal) must keep declining unchanged — each has an existing pinned test in
  `NativeReferenceIncludeTests.cs` (`DeclinedShapeDescriptions`/`GetDeclinedShapeBuilder` theory rows, plus
  dedicated `[Fact]`s) that must stay green throughout.

---

## Task 1: Chain-aware recognizer and confirmation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  - Replace the call site at lines 365-374 (the `if (IsSingleLevelReferenceIncludeSelector(selector))` block
    inside the trailing-`Select` dispatch).
  - Replace `IsSingleLevelReferenceIncludeSelector` (lines 826-833) with a new
    `TryGetReferenceIncludeChain` returning the recognized chain (or `null`).
  - Replace `TryConfirmReferenceInclude` (lines 873-951) with a new `TryConfirmReferenceIncludeChain` that
    validates and confirms every navigation in the chain.
  - `HasNonEmbeddedThenInclude` (lines 976-989) is unchanged and reused per chain level.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Produces: `TryGetReferenceIncludeChain(LambdaExpression selector) : List<IncludeExpression>?` — outer-to-inner
  order (last `.Include()` call first), or `null` if the selector isn't a pure reference-Include chain (a
  collection or embedded navigation at any level, a real `ThenInclude` off any level, or a base that isn't a
  pure `.Outer`* chain reaching the parameter, all cause `null`).
- Produces: `TryConfirmReferenceIncludeChain(MongoQueryExpression mongoQueryExpression, List<IncludeExpression> chain) : bool`
  — validates every navigation, confirms all-or-nothing, calls `mongoQueryExpression.Select.MarkReferenceIncludeConfirmed()`
  exactly once per chain entry on success.
- Consumes (unchanged, pre-existing): `MongoQueryExpression.GetPendingLookups()`, `.AddLookup(LookupExpression)`,
  `.Joins`, `.InnerCollections`, `.CollectionExpression.EntityType`; `MongoSelectDefinition.HasTerminalOperator`,
  `.SawNonBareJoinInner`, `.MarkReferenceIncludeConfirmed()`; `LookupExpression.GetLookupAlias(INavigation)`,
  `.LookupAliasPrefix`; `HasNonEmbeddedThenInclude(Expression)` (this file, unchanged).

- [ ] **Step 1: Read the exact current code you're replacing, to confirm line numbers haven't drifted**

Run:
```bash
sed -n '360,380p;820,835p;870,955p' src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
```
Confirm the call site (`if (IsSingleLevelReferenceIncludeSelector(selector))` … `TryConfirmReferenceInclude`),
`IsSingleLevelReferenceIncludeSelector`'s body, and `TryConfirmReferenceInclude`'s body match what's quoted in
this task before editing. If they've drifted, adjust line-number references only — the logic below is
unaffected by drift.

- [ ] **Step 2: Write the failing tests first (TDD red state already exists naturally)**

Two rows in the existing `DeclinedShapeDescriptions` theory
(`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs:167-168`,
`"sibling reference Includes"` and `"same-target sibling Includes"`) currently assert these exact shapes
**decline** under `NativeOnly`. Do not touch that theory yet (Task 2 flips it) — instead add two new,
temporary `[Fact]`s right after `A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines`
(around line 151) that assert the shapes go **native**, so they fail red against today's code:

```csharp
[Fact]
public void Sibling_reference_Includes_go_native()
{
    using var db = CreateContext(MongoQueryMode.NativeOnly, nameof(Sibling_reference_Includes_go_native));

    var results = db.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

    Assert.Equal(2, results.Count);
    Assert.All(results, l => Assert.NotNull(l.Order));
    Assert.All(results, l => Assert.NotNull(l.Product));
}

[Fact]
public void Same_target_sibling_reference_Includes_go_native()
{
    using var db = CreateContext(MongoQueryMode.NativeOnly, nameof(Same_target_sibling_reference_Includes_go_native));

    var results = db.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

    Assert.Equal(1, results.Count);
    Assert.All(results, d => Assert.NotNull(d.Author));
    Assert.All(results, d => Assert.NotNull(d.Editor));
    Assert.All(results, d => Assert.NotEqual(d.Author.Id, d.Editor.Id));
}
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Sibling_reference_Includes_go_native|FullyQualifiedName~Same_target_sibling_reference_Includes_go_native"
```
Expected: both FAIL with `NativeTranslationNotSupportedException` ("Query is not natively representable").

- [ ] **Step 4: Replace the recognizer**

Replace `IsSingleLevelReferenceIncludeSelector` (the whole method at lines 826-833) with:

```csharp
/// <summary>
/// Recognizes <paramref name="selector"/> as a chain of one or more single-level reference <c>Include</c>s
/// stacked via <see cref="IncludeExpression.EntityExpression"/> nesting — EF's nav-expansion shape for N
/// sibling reference Includes on one query (e.g. <c>Docs.Include(d => d.Author).Include(d => d.Editor)</c>
/// or <c>Lines.Include(l => l.Order).Include(l => l.Product)</c>). Each level's own navigation must be a
/// non-collection, non-embedded reference — a collection or embedded navigation at ANY level (e.g. the
/// "reference + collection" combo, <c>Orders.Include(o => o.Buyer).Include(o => o.Lines)</c>) returns
/// <see langword="null"/> for the WHOLE chain, unchanged from before. The walk bottoms out at a pure
/// <c>.Outer</c>* member-access chain reaching the selector's own parameter — the TransparentIdentifier
/// plumbing EF builds for N stacked joins (one <c>.Outer</c> hop per join beyond the innermost). A single
/// reference Include (today's only supported case) is the N=1 case: zero or one <c>.Outer</c> hops,
/// handled by the exact same walk, so this supersedes <c>IsSingleLevelReferenceIncludeSelector</c> rather
/// than sitting alongside it.
/// <para>
/// Returns the recognized levels OUTER-TO-INNER (the LAST <c>.Include()</c> call first) — the order the
/// nesting is discovered in, not join-registration order. Callers that need per-navigation validation
/// (composite key, <c>ThenInclude</c>, etc.) can iterate in this order safely since each level's checks are
/// independent of the others.
/// </para>
/// </summary>
internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
{
    if (selector.Parameters.Count != 1
        || !selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal))
    {
        return null;
    }

    var parameter = selector.Parameters[0];
    var levels = new List<IncludeExpression>();
    var body = selector.Body;

    while (body is IncludeExpression { Navigation: INavigation navigation } include)
    {
        if (navigation.IsCollection || navigation.IsEmbedded())
        {
            return null;
        }

        levels.Add(include);
        body = include.EntityExpression;
    }

    if (levels.Count == 0)
    {
        return null;
    }

    var current = body;
    while (current is MemberExpression { Member.Name: "Outer" } outerAccess)
    {
        current = outerAccess.Expression;
    }

    return current == parameter ? levels : null;
}
```

Update the call site (lines 365-374) from:
```csharp
        if (IsSingleLevelReferenceIncludeSelector(selector))
        {
            if (!TryConfirmReferenceInclude(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
```
to:
```csharp
        if (TryGetReferenceIncludeChain(selector) is { } referenceIncludeChain)
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, referenceIncludeChain))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
```

- [ ] **Step 5: Replace the confirmation function**

Replace `TryConfirmReferenceInclude` (the whole method at lines 873-951, but keep its XML doc comment's
factual content — trim to describe the chain version) with:

```csharp
private static bool TryConfirmReferenceIncludeChain(
    MongoQueryExpression mongoQueryExpression,
    List<IncludeExpression> chain)
{
    // Declines that apply to the WHOLE chain, not per-navigation — unchanged semantics from the
    // single-Include recognizer: HasTerminalOperator/SawNonBareJoinInner are Select-level flags, and
    // Joins.Count must equal chain.Count exactly (not InnerCollections.Count, which is entity-type-keyed
    // and would wrongly collapse two same-target sibling joins into one — see MongoQueryExpression.Lookup.cs;
    // Joins is a list, one entry per join, so it correctly distinguishes N=1 single-Include, N=2+ siblings
    // (same or different target types), AND a mismatched case like a user Join plus a downstream Include
    // targeting the same type, which must still decline — "user join with downstream Include" in
    // NativeReferenceIncludeTests.DeclinedShapeDescriptions).
    if (mongoQueryExpression.Select.HasTerminalOperator
        || mongoQueryExpression.Select.SawNonBareJoinInner
        || mongoQueryExpression.Joins.Count != chain.Count)
    {
        return false;
    }

    var pendingByAlias = mongoQueryExpression.GetPendingLookups().ToDictionary(l => l.As);
    var newLookups = new List<LookupExpression>();

    foreach (var include in chain)
    {
        var navigation = (INavigation)include.Navigation!;

        if (navigation.ForeignKey.Properties.Count != 1                        // composite FK
            || navigation.ForeignKey.PrincipalKey.Properties.Count != 1        // composite PK
            || HasNonEmbeddedThenInclude(include.NavigationExpression)         // a real ThenInclude riding along
            || navigation.DeclaringEntityType != mongoQueryExpression.CollectionExpression.EntityType
            || !mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType))
        {
            return false;
        }

        var alias = LookupExpression.GetLookupAlias(navigation);
        if (pendingByAlias.TryGetValue(alias, out var existing))
        {
            // Already registered by TranslateJoinCore's multi-join flattening (fires unconditionally once
            // Joins.Count > 1, independent of Include confirmation — see RebindInnerShaperToOuterQuery's
            // isSecondOrLaterJoin branch). Confirm it, don't re-register it.
            if (!existing.ForceUnwind)
            {
                return false;
            }
        }
        else
        {
            // The single-join case (chain.Count == 1): TranslateJoinCore never flattens a lone join, so
            // nothing is registered yet. Build and add it here, exactly as the pre-chain single-Include
            // recognizer did.
            var lookup = new LookupExpression(navigation, forceUnwind: true)
            {
                PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired
            };

            if (lookup.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            newLookups.Add(lookup);
        }
    }

    foreach (var lookup in newLookups)
    {
        mongoQueryExpression.AddLookup(lookup);
    }

    for (var i = 0; i < chain.Count; i++)
    {
        mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
    }

    return true;
}
```

Add `using System.Linq;` at the top of the file if not already present (check first — this file already uses
`FirstOrDefault` per `RebindInnerShaperToOuterQuery`, so it almost certainly is).

- [ ] **Step 6: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -20`
Expected: 0 errors. If `IsSingleLevelReferenceIncludeSelector`/`TryConfirmReferenceInclude` are referenced
anywhere else in the file or elsewhere in the codebase, the build will fail with an undefined-reference error —
resolve by updating those call sites to the new names/signatures (there should be exactly one call site, the
one replaced in Step 4; confirm with `grep -rn "IsSingleLevelReferenceIncludeSelector\|TryConfirmReferenceInclude\b" src/`
before declaring this step done).

- [ ] **Step 7: Run the new tests to verify they pass, and the full existing file to verify no regressions**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```
Expected: ALL tests pass, including the two new ones from Step 2 and every pre-existing decline test
(`A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines`, the `DeclinedShapeDescriptions` theory
— still asserting decline for `"sibling reference Includes"`/`"same-target sibling Includes"` at this point,
since Task 2 hasn't flipped them yet, so this step's expectation is a CONTRADICTION with Step 2's new facts —
see the note below).

**Note on an expected transient conflict:** the `DeclinedShapeDescriptions` theory rows for `"sibling reference
Includes"` and `"same-target sibling Includes"` (lines 167-168) will now FAIL, because this task makes those
exact shapes go native, contradicting the theory's decline assertion. This is expected — proceed to Task 2
immediately, which removes those two rows and replaces them with positive tests (and can absorb/delete the two
temporary `[Fact]`s from Step 2, since the flipped theory-adjacent tests in Task 2 supersede them). Do not
consider Task 1 "done" (in the sense of a clean full-suite run) until Task 2 also lands — but do commit Task 1
and Task 2 together, or in immediate succession, so no commit in history has a failing test.

- [ ] **Step 8: Commit (paired with Task 2 — see Task 2's own commit step; do not commit yet if running tasks
  back-to-back in one sitting)**

If Task 2 is being done in the same sitting (recommended), skip committing here and commit once at the end of
Task 2. If pausing between tasks, commit with:
```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-392: recognize sibling reference Include chains (WIP, DeclinedShapeDescriptions rows updated in next commit)"
```

---

## Task 2: Flip the declined-shape rows to positive differential tests

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:**
- Consumes: `CreateContext(MongoQueryMode, string, ...)` (existing helper, lines 932-1016), the
  `Native_and_DriverLinq_agree_on_...` differential idiom (lines 910-929), `Line`/`Product`/`Doc`/`Buyer`
  model classes (lines 1031-1126, unchanged).
- Produces: two new differential `[Fact]`s; removes two rows from `DeclinedShapeDescriptions` and their
  builders from `GetDeclinedShapeBuilder`.

- [ ] **Step 1: Add dangling-FK seed rows so the differential tests actually exercise inner-vs-left-outer
  unwind semantics independently per lookup, not just "doesn't throw"**

In `CreateContext` (around line 992-1001), the current seed has exactly one `Line` per product/order pair and
one `Doc` with both FKs valid — no dangling FK on either side, so a differential test today could pass
vacuously even if one lookup's unwind kind were wrong. Add one dangling-FK row per test model:

Replace:
```csharp
        database.MongoDatabase.GetCollection<Line>(linesName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = product1Id, Quantity = 2 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order2Id, ProductId = product1Id, Quantity = 3 },
        ]);

        database.MongoDatabase.GetCollection<Doc>(docsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = buyer2Id, Title = "Doc1" },
        ]);
```
with:
```csharp
        var danglingOrderId = ObjectId.GenerateNewId(); // never inserted: dangling FK, Line -> Order side.
        var danglingProductId = ObjectId.GenerateNewId(); // never inserted: dangling FK, Line -> Product side.

        database.MongoDatabase.GetCollection<Line>(linesName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = product1Id, Quantity = 2 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order2Id, ProductId = product1Id, Quantity = 3 },
            // EF-392 (sibling reference Includes): one dangling FK per side, on DIFFERENT rows, so a
            // differential test can prove each lookup's own required-FK inner-unwind semantics
            // independently rather than only proving "doesn't throw".
            new() { Id = ObjectId.GenerateNewId(), OrderId = danglingOrderId, ProductId = product1Id, Quantity = 1 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = danglingProductId, Quantity = 1 },
        ]);

        database.MongoDatabase.GetCollection<Doc>(docsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = buyer2Id, Title = "Doc1" },
            // EF-392 (same-target sibling reference Includes): a dangling AuthorId and a dangling EditorId
            // on separate rows, so a differential test can prove the two _lookup_Author/_lookup_Editor
            // fields are independently scoped (neither lookup accidentally reads the other's field).
            new() { Id = ObjectId.GenerateNewId(), AuthorId = ObjectId.GenerateNewId(), EditorId = buyer2Id, Title = "Doc2" },
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = ObjectId.GenerateNewId(), Title = "Doc3" },
        ]);
```
(`Order`/`Product`/`Author`/`Editor` are all REQUIRED reference navigations on this fixture — check
`Line.Order`/`Line.Product`/`Doc.Author`/`Doc.Editor` property declarations at lines 1053-1074: all
non-nullable reference types with no `?` — so a dangling FK means the row is DROPPED by the inner unwind, not
nulled. This matches `Order.Buyer`'s existing required-FK-drops-the-row precedent at line 68-70.)

- [ ] **Step 2: Run to verify the seed change alone doesn't break anything**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```
Expected: the two Task-1 `[Fact]`s and the differential-Native/DriverLinq test still pass; the two
`DeclinedShapeDescriptions` theory rows for sibling Includes still FAIL (same as at the end of Task 1 — this
seed change doesn't fix that, Step 3 below does).

- [ ] **Step 3: Remove the two now-obsolete declined-shape rows and their builders**

In `DeclinedShapeDescriptions` (lines 165-183), remove these two lines:
```csharp
        "sibling reference Includes",
```
and
```csharp
        "same-target sibling Includes",
```
(leave `"ThenInclude / transitive"`, `"after a terminal"`, `"reference + collection"`, and
`"user join with downstream Include"` untouched — those remain declined.)

In `GetDeclinedShapeBuilder` (lines 185-291), remove the two corresponding switch arms and their large comment
blocks: the `"sibling reference Includes" => db => db.Lines.Include(l => l.Order).Include(l => l.Product),`
arm and its preceding comment (lines 188-223), and the `"same-target sibling Includes" => db => db.Docs.Include(d => d.Author).Include(d => d.Editor),`
arm and its preceding comment (lines 224-257). Leave the remaining arms (`"ThenInclude / transitive"`,
`"after a terminal"`, `"reference + collection"`, `"user join with downstream Include"`) and the trailing
`_ => throw ...` unchanged.

- [ ] **Step 4: Delete the two temporary Task-1 `[Fact]`s and replace with proper differential tests**

Remove `Sibling_reference_Includes_go_native` and `Same_target_sibling_reference_Includes_go_native` (added in
Task 1 Step 2) and add these two in their place, right after
`A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines`:

```csharp
[Fact]
public void Sibling_reference_Includes_go_native_with_correct_data()
{
    // EF-392 (Include-breadth remainder): different target types (Order, Product) — the smallest sibling
    // shape. NativeOnly succeeding proves native, not fallback; the Native == DriverLinq comparison proves
    // the two independently-scoped $lookups (each with its own required-FK inner unwind) return the same
    // rows a working, well-understood driver-LINQ oracle would.
    using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
        nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_NativeOnly");
    var nativeOnlyResults = nativeOnly.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

    // 4 lines seeded: 2 clean, 1 with a dangling OrderId, 1 with a dangling ProductId. Both Order and
    // Product are REQUIRED references, so each dangling FK drops its own row via an inner unwind — 2 rows
    // survive.
    Assert.Equal(2, nativeOnlyResults.Count);
    Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Order));
    Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Product));

    using var nativeDb = CreateContext(MongoQueryMode.Native,
        nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_Native");
    var nativeResults = nativeDb.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

    using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
        nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_DriverLinq");
    var driverResults = driverDb.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

    Assert.Equal(driverResults.Count, nativeResults.Count);
    Assert.All(nativeResults, l => Assert.NotNull(l.Order));
    Assert.All(nativeResults, l => Assert.NotNull(l.Product));
}

[Fact]
public void Same_target_sibling_reference_Includes_go_native_with_correct_data()
{
    // EF-392 (Include-breadth remainder): SAME target type (Buyer) for both Author and Editor — proves
    // InnerCollections' entity-type keying (which would collapse these two joins into one dictionary
    // entry) is NOT what this recognizer relies on for correctness; Joins.Count (a list, one entry per
    // join) and the navigation-keyed $lookup alias are.
    using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
        nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_NativeOnly");
    var nativeOnlyResults = nativeOnly.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

    // 3 docs seeded: 1 clean, 1 with a dangling AuthorId, 1 with a dangling EditorId. Both are required
    // references, so each dangling FK drops its own row — 1 row survives, and it must have DIFFERENT,
    // correctly-scoped Author and Editor navigations (not one field accidentally reused for both).
    Assert.Equal(1, nativeOnlyResults.Count);
    var only = Assert.Single(nativeOnlyResults);
    Assert.NotNull(only.Author);
    Assert.NotNull(only.Editor);
    Assert.NotEqual(only.Author.Id, only.Editor.Id);

    using var nativeDb = CreateContext(MongoQueryMode.Native,
        nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_Native");
    var nativeResults = nativeDb.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

    using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
        nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_DriverLinq");
    var driverResults = driverDb.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

    Assert.Equal(driverResults.Count, nativeResults.Count);
    Assert.All(nativeResults, d => Assert.NotEqual(d.Author.Id, d.Editor.Id));
}
```

- [ ] **Step 5: Build and run the whole file**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -20
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeReferenceIncludeTests"
```
Expected: ALL tests in the file pass now, including the `DeclinedShapeDescriptions` theory (now without the
two removed rows) and the two new differential tests.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
git commit -m "EF-392: native sibling reference Include chains (different- and same-target)"
```

---

## Task 3: Confirm the untouched declines really stay untouched

**Files:**
- Test only, no modifications expected: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs`

**Interfaces:** none new — this task is verification only.

- [ ] **Step 1: Run the full remaining `DeclinedShapeDescriptions` theory explicitly**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Declined_shapes_throw_under_NativeOnly_and_match_DriverLinq_under_Native"
```
Expected: passes for all remaining rows (`"ThenInclude / transitive"`, `"after a terminal"`,
`"reference + collection"`, `"user join with downstream Include"`) — 4 rows, all green. If
`"reference + collection"` (`Orders.Include(o => o.Buyer).Include(o => o.Lines)`) now FAILS (i.e. wrongly goes
native), that means `TryGetReferenceIncludeChain`'s collection-navigation guard didn't fire as designed — stop
and re-open Task 1 Step 4 rather than patching around it here; do not weaken this test.

- [ ] **Step 2: Run `Two_joins_onto_the_same_target_stay_declined` and any other pre-existing test referencing
  the same-target-collision scenario by name**

Run:
```bash
grep -n "Two_joins_onto_the_same_target\|Composite_FK_and_PK_still_declines" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceIncludeTests.cs
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Two_joins_onto_the_same_target|FullyQualifiedName~Composite_FK_and_PK"
```
Expected: both pass unchanged.

- [ ] **Step 3: No commit needed** — this task is verification only; if everything passed, nothing changed.

---

## Task 4: Full multi-EF regression

**Files:** none — verification only.

- [ ] **Step 1: Build all three EF versions**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" 2>&1 | tail -5
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" 2>&1 | tail -5
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5
```
Expected: 0 errors on all three.

- [ ] **Step 2: Run the full Query test suite on all three EF versions**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```
Expected: 0 failures on all three (skips unrelated to this change — encryption tests gated on
`CRYPT_SHARED_LIB_PATH` — are fine).

- [ ] **Step 3: Run the full unit + functional + spec suite on EF10 at minimum (all three if time allows,
  or invoke the `/test-all` skill for all three in parallel)**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: 0 failures.

- [ ] **Step 4: No further commit** — Tasks 1-2 already committed the real change; this task is a
  verification gate only. If any regression surfaces, return to systematic-debugging on the specific failure
  rather than patching blindly.
