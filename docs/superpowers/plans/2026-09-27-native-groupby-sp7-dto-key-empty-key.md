# Native GroupBy SP7: DTO Keys & Zero-Part-Key Readback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate two currently-declining `GroupBy` shapes natively — a `MemberInitExpression` (ctor-DTO)
key selector, and a bare `g.Key` readback over a zero-part (`new{}`) key — moving 2
`NorthwindGroupByQueryMongoTest` methods off the driver-LINQ fallback: `GroupBy_Dto_as_key_Select_Sum`,
`GroupBy_empty_key_Aggregate_Key`.

**Architecture:** Two independent, narrow fixes in `NativeGroupByBinder.cs`:

- **Task 1** adds a `MemberInitExpression` case to `TryBindGroupKey`'s key-selector switch, alongside the
  existing `NewExpression{Arguments.Count:0}` (zero-part) and `NewExpression{Members:{Count:>0}}` (anonymous
  composite) cases. It walks the `MemberInitExpression`'s `.Bindings` (each expected to be a plain
  `MemberAssignment`) exactly the way the existing composite-key case walks `NewExpression.Arguments`/
  `.Members`, calling the SAME `TryBindKeyPartValue` per binding value — so a value-converted or
  non-default-`BsonRepresentation` key member declines for free (that guard lives inside
  `TryTranslateValue`/`TryBindKeyPartValue`, not duplicated here).
- **Task 2** fixes `TryGetKeyMemberPath`'s zero-part-key branch: it currently returns `path = null`
  unconditionally for a zero-part key (`keyParts.Count == 0`), which makes every flatten/ordering/comparison
  call site decline a bare `g.Key` read over `GroupBy(o => new { })`. The fix drops the `keyParts.Count == 0`
  clause — the group's own empty `_id: {}` document IS the correct read-back value for an empty anonymous-type
  key, so a zero-part key should resolve to `"_id"` the same way a non-empty key already does. This makes a
  zero-part key's `g.Key` reachable at THREE more call sites than the flatten loop alone (the ordering
  carve-out, `TryTranslateGroupProjectionConditionOrValue`'s comparison arms, and `TryBindGroupSideOperand`'s
  HAVING/terminal-predicate arm) — two of those (`ResolveKeyMemberSerializationProperty` and
  `TryBindGroupSideOperand`) unconditionally index `keyParts[0]` once `keyPath == "_id"` is reached, which
  would throw `IndexOutOfRangeException` for a genuinely EMPTY `keyParts` list. Task 2 guards both.

A third SP7 candidate test, `Odata_groupby_empty_key`, is explicitly OUT OF SCOPE for this plan — see "Deferred
out of scope" below.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$project`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP7 section — this
plan narrows and corrects that section's scope; see "Deviations from the spec" below)

## Deviations from the spec

- The spec's SP7 lists 3 tests, including `Odata_groupby_empty_key`. Investigation for this plan found the
  spec's own gap analysis for that test wrong on two points: (1) its key, `new NoGroupByWrapper()`, has no
  object-initializer braces, so it compiles to a plain `NewExpression` with `Arguments.Count == 0` — it
  ALREADY matches the existing zero-part-key case, no new key-side code needed; (2) its projection,
  `.Select(e => new NoGroupByAggregationWrapper { Container = new LastInChain { Name = "TotalAmount", Value =
  e.Sum(e => (decimal)e.OrderID) } })`, nests a SECOND `MemberInitExpression` (with a constant binding and an
  accumulator-call binding) as one projection member's VALUE — this is not "confirm the flatten already
  handles it" as the spec assumed; it needs genuinely new code, AND the existing `MongoDocumentConstructionExpression`
  shaper machinery (`MongoProjectionBindingRemovingExpressionVisitor.BuildDocumentConstructionExpression` /
  `ReadDocumentConstructionMemberTyped`) that a naive read of the spec might suggest reusing does NOT support
  it as-is: `ReadDocumentConstructionMemberTyped` hard-casts every member's value to `MongoFieldExpression`
  (`(MongoFieldExpression)value`), so it cannot read a nested construction, a group accumulator's
  `MongoElementRefExpression` output, or even a bare constant binding. Making that work is real, riskier
  cross-cutting shaper work (it is shared with ordinary, non-GroupBy nested-DTO Select projections, EF-447),
  not a small extension. Ruling (recorded per the project's stacked-PR/SDD convention): ship the two genuinely
  small, confirmed-narrow gaps as this SP7, and file `Odata_groupby_empty_key`'s nested-construction gap as
  its own follow-up ticket/slice with these findings — do not attempt it here.

## Global Constraints

- **`Native == DriverLinq` invariant** — both target tests' `AssertQuery` must keep passing under
  `MongoQueryMode.Native` exactly as today (via fallback), in addition to newly passing under
  `MongoQueryMode.NativeOnly`.
- **A value-converted or non-default-`BsonRepresentation` key member must still decline, never silently
  mis-serialize.** Task 1's new `MemberInitExpression` case must call the SAME `TryBindKeyPartValue` helper
  the existing composite-key case already uses — do not write a parallel, possibly-weaker translation path.
- **No new `MongoExpression` subtype** — both tasks reuse existing IR (`MongoGroupingKeyPart`,
  `MongoElementRefExpression`). Do not touch `MongoExpressionNodeCoverageTests.cs`.
- **Re-run `NorthwindGroupByQueryMongoTest` under `MONGODB_EF_NATIVE_ONLY=1` after each task and recount the
  `NativeOnly` failure list from the log** — do not assume a target test's disappearance from the failure list
  means nothing else changed; diff the full method-name list against a captured "before" run (per the
  branch-review-coverage lesson: a green suite alone does not prove a fix's actual reach).
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.
- Preserve file BOMs on every file this plan touches.

## Review Focus

- **A `MemberInitExpression` key whose `NewExpression` base itself takes constructor arguments** (a DTO with
  both a parameterized ctor AND an object initializer, e.g. `new Foo(1) { Bar = x }`) — Task 1 must decline
  this shape (no target test needs it, and mixing ctor args with initializer bindings has no established
  key-part-naming convention in this codebase) rather than silently dropping the ctor argument.
- **A `MemberInitExpression` key binding that is a `MemberMemberBinding` or `MemberListBinding`** (nested
  object/collection initializer syntax, e.g. `new Foo { Bar = { Baz = 1 } }`) — Task 1 must decline the WHOLE
  key, not silently skip or mis-bind that one part.
- **The zero-part-key fix reaching `ResolveKeyMemberSerializationProperty`/`TryBindGroupSideOperand` with an
  EMPTY `keyParts` list must not crash** (`IndexOutOfRangeException` from an unconditional `keyParts[0]`) —
  Task 2's own test must exercise a HAVING/terminal-predicate comparison against a zero-part key's `g.Key`
  (e.g. `GroupBy(o => new { }).Where(g => g.Key == null)`), not just the flatten-loop shape the target test
  itself uses.
- **A zero-part key's `g.Key` used in an `OrderBy` composed directly on the ungrouped `GroupBy` result**
  (`GroupBy(o => new { }).OrderBy(g => g.Key).Select(...)`) newly resolves to `"_id"` after Task 2 (previously
  declined) — since a zero-part key always produces exactly one group, ordering by it is a safe no-op; Task 2
  must have a test confirming this doesn't crash or misorder (there is nothing TO misorder — one group).
- **`TryResolveKeyReferenceAsRawExpression` (line ~686) has the SAME unconditional `keyParts[0]` indexing for
  a zero-part key's `g.Key` referenced INSIDE an accumulator's per-element condition** (e.g. `g.Count(e =>
  g.Key == null)`) — this is a PRE-EXISTING gap, reachable independently of Task 2's change (it resolves
  against the per-element expression tree, not through `TryGetKeyMemberPath` at all), so it is OUT OF SCOPE
  for this plan. Flag it with a `KNOWN BUG` comment at Task 2's own commit, matching SP6's precedent for a
  found-but-out-of-scope gap, rather than silently leaving it undocumented.

## Task 1: `MemberInitExpression` DTO key selector

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs:50-103` (`TryBindGroupKey`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs:525-533`
  (`GroupBy_Dto_as_key_Select_Sum` override — baseline regeneration only, Step group below)

**Interfaces:**
- Consumes: `TryBindKeyPartValue(Expression, MongoExpressionTranslator, out MongoExpression?)` (existing,
  unchanged) — translates one key-part VALUE expression (a plain member access, computed expression, etc.),
  already enforcing default-key-serialization via `TryTranslateValue`.
- Produces: no new public/internal signatures — `TryBindGroupKey`'s own contract (`bool`, populates
  `select.PendingGroupKey`) is unchanged; only its switch grows a new case.

- [ ] **Step 1: Write a failing functional test proving the DTO-key shape currently falls back**

Add to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs`, inside
the `NativeGroupByCtorProjectionTests` class (after the existing `CustomerIdOnly` private class declaration,
before `UniqueCollectionName`):

```csharp
    // A MemberInitExpression (object-initializer) DTO key — new NominalType { A = ..., B = ... } — as opposed
    // to the existing ctor-only DTO tests above, whose keys are all plain scalar members.
    private class CustomerEmployeeKey
    {
        public string CustomerId { get; set; } = "";
        public int EmployeeId { get; set; }
    }
```

Then add this test method after `GroupBy_select_with_two_argument_ctor_only_dto_goes_native`:

```csharp
    [Fact]
    public void GroupBy_select_with_member_init_dto_key_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_member_init_dto_key_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "EmployeeId", 2 }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the MemberInitExpression key selector went native.
        var results = db.Entities
            .GroupBy(o => new CustomerEmployeeKey { CustomerId = o.CustomerId, EmployeeId = 0 })
            .Select(g => new { Sum = g.Sum(o => o.Total), g.Key })
            .AsEnumerable()
            .OrderBy(r => r.Key.CustomerId)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Key.CustomerId);
        Assert.Equal(30m, results[0].Sum);
        Assert.Equal("B", results[1].Key.CustomerId);
        Assert.Equal(5m, results[1].Sum);
    }
```

Note: `EmployeeId` is deliberately keyed as a constant (`0`) in the `GroupBy` selector rather than a second
real per-row field — this test only needs to prove a MULTI-BINDING `MemberInitExpression` key goes native and
reads back correctly by NAME (not position), which a constant binding proves just as well as a second real
field and keeps the fixture data simpler. Order's `EmployeeId` field does not need to exist in the fixture
data at all for this reason.

- [ ] **Step 2: Run it to confirm it fails today**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_select_with_member_init_dto_key_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: FAIL with `NativeTranslationNotSupportedException` (the `MemberInitExpression` key falls back to
driver-LINQ today, which `NativeOnly` forbids).

- [ ] **Step 3: Add the `MemberInitExpression` case to `TryBindGroupKey`**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, in the `switch
(keySelector.Body)` inside `TryBindGroupKey` (currently lines 66-99), add a new case AFTER the
`NewExpression { Members: { Count: > 0 } members }` case (line 78-87) and BEFORE the `default:` arm:

```csharp
            // A MemberInitExpression DTO key (GroupBy(o => new NominalType { A = ..., B = ... })) — a
            // composite key whose parts each carry the bound member's name, same as the anonymous-type
            // NewExpression case above, but for a real (non-anonymous) type with an object initializer. Each
            // binding must be a plain MemberAssignment (a nested MemberMemberBinding/MemberListBinding — e.g.
            // `new Foo { Bar = { Baz = 1 } }` — has no single translatable VALUE and declines the whole key,
            // not just that part). The base NewExpression must take zero constructor arguments — a DTO
            // combining a parameterized ctor with an initializer has no established key-part-naming
            // convention in this codebase and is declined rather than silently dropping the ctor args.
            case MemberInitExpression { NewExpression.Arguments.Count: 0 } memberInit:
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;

                    // EF-322 SP7: each key part is translated via TryBindKeyPartValue — the SAME helper the
                    // anonymous-type composite-key case above uses, so a value-converted or non-default-
                    // represented key member declines for free (the guard lives inside TryTranslateValue).
                    if (!TryBindKeyPartValue(assignment.Expression, translator, out var memberPartValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(assignment.Member.Name, memberPartValue));
                }

                break;

```

Also update the `default:` arm's doc comment (currently line 89-93) to remove the now-stale "a
`MemberInitExpression` DTO key... still decline[s]" clause, and update `TryBindGroupKey`'s own XML-doc summary
(lines 43-49) to mention the new case. Replace:

```csharp
            // A bare member, EF.Property(...) call, computed expression (date-part/arithmetic/cast), literal
            // constant, or captured query parameter. A genuine multi-argument constructor call (NewExpression
            // with Members == null, e.g. new OrderKey(o.Country, o.Year)) also reaches here as the WHOLE
            // NewExpression — see TryBindKeyPartValue's own remarks for why that, and a MemberInitExpression
            // DTO key, both still decline.
```

with:

```csharp
            // A bare member, EF.Property(...) call, computed expression (date-part/arithmetic/cast), literal
            // constant, or captured query parameter. A genuine multi-argument constructor call (NewExpression
            // with Members == null, e.g. new OrderKey(o.Country, o.Year)) also reaches here as the WHOLE
            // NewExpression — see TryBindKeyPartValue's own remarks for why that still declines. A
            // MemberInitExpression DTO key is handled by its own case above, EF-322 SP7.
```

- [ ] **Step 4: Run the functional test to confirm it now passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_select_with_member_init_dto_key_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: PASS.

- [ ] **Step 5: Write a unit test pinning the decline for the two Review Focus edge cases**

Add to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs` (read
the existing file first to match its fixture/helper conventions — it already has helpers for building a
`MongoQueryExpression` and invoking `NativeGroupByBinder.TryBindGroupKey` directly against a hand-built
`LambdaExpression`). Add two test methods:

1. A `MemberInitExpression` key whose base `NewExpression` has a non-zero `Arguments.Count` (a DTO with a
   parameterized constructor AND an initializer) — assert `TryBindGroupKey` returns `false`.
2. A `MemberInitExpression` key with a `MemberMemberBinding` (nested initializer, e.g. built via
   `Expression.MemberBind` on a sub-object member) — assert `TryBindGroupKey` returns `false`.

If the existing test file has no direct helper for constructing a `MemberInitExpression`/`MemberMemberBinding`
by hand, build the `LambdaExpression` directly with `System.Linq.Expressions` APIs
(`Expression.MemberInit`, `Expression.Bind`, `Expression.MemberBind`) the same way the file's existing tests
construct their own `NewExpression`-keyed lambdas — read those first and mirror the pattern exactly.

- [ ] **Step 6: Run the new and existing unit tests**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests" \
  --logger "console;verbosity=normal"
```

Expected: PASS, including the two new tests.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: translate a MemberInitExpression DTO key selector for GroupBy"
```

## Task 2: Zero-part-key `g.Key` readback

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs:357-399`
  (`TryGetKeyMemberPath`), `:520-534` (`ResolveKeyMemberSerializationProperty`), `:1359-1399`
  (`TryBindGroupSideOperand`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs:905-913`
  (`GroupBy_empty_key_Aggregate_Key` override — baseline regeneration only, Step group below)

**Interfaces:**
- Consumes: nothing new from Task 1 — independent fix.
- Produces: `TryGetKeyMemberPath` now returns `path = "_id"` (not `null`) for a zero-part key regardless of
  `allowWholeKeyRead`; every existing caller (the flatten loop, the ordering carve-out,
  `TryTranslateGroupProjectionConditionOrValue`, `TryBindGroupSideOperand`) is unchanged in signature.

- [ ] **Step 1: Write a failing functional test proving the zero-part-key `g.Key` shape currently falls back**

Add to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`, inside the
`NativeGroupByTests` class. This file's own fixture conventions (read the file first, e.g. lines 40-77):
a private `Order` class with `Country`/`Year`/`Amount`/etc., a `SeedOrders()` static factory, and a
`CreateContext(Order[] seed, MongoQueryMode mode, string name)` helper that seeds a fresh uniquely-named
collection and returns a ready `SingleEntityDbContext<Order>` — reuse these directly, do not invent a
parallel `BsonDocument`-based setup. Add:

```csharp
    [Fact]
    public void GroupBy_empty_key_bare_Key_readback_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_bare_Key_readback_goes_native));

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the bare g.Key read over a zero-part key went native.
        var results = db.Entities
            .GroupBy(o => new { })
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
            .ToList();

        Assert.Single(results);
        Assert.Equal(675m, results[0].Sum); // sum of SeedOrders()'s 5 Amount values: 100+200+50+25+300
    }

    [Fact]
    public void GroupBy_empty_key_Where_on_bare_Key_goes_native()
    {
        // Review Focus: a zero-part key's g.Key reaching the HAVING/terminal-predicate path
        // (TryBindGroupSideOperand) with an EMPTY keyParts list must not crash.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Where_on_bare_Key_goes_native));

        var results = db.Entities
            .GroupBy(o => new { })
            .Where(g => g.Key == null)
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
            .ToList();

        Assert.Single(results);
        Assert.Equal(675m, results[0].Sum);
    }
```

Note: `g.Key == null` compiles against the empty anonymous type `new { }` — comparing a struct-like anonymous
type instance to `null` is legal C# (anonymous types are reference types) and is always false at runtime for
a non-null instance, but this test only needs to prove the SHAPE reaches native code without throwing
`IndexOutOfRangeException` (this plan's own Review Focus item), not that the comparison is semantically
useful — `AssertQuery`-style behavioral parity is covered by the spec-test baseline in the Verify step below,
which is why this functional test can safely assert on the row still being present rather than the (always
false, hence non-restrictive) predicate's own truth value.

- [ ] **Step 2: Run both new tests to confirm they fail today**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_empty_key_bare_Key_readback_goes_native|FullyQualifiedName~GroupBy_empty_key_Where_on_bare_Key_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: both FAIL with `NativeTranslationNotSupportedException`.

- [ ] **Step 3: Fix `TryGetKeyMemberPath`'s zero-part-key branch**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, change line 380 from:

```csharp
            path = keyParts.Count == 0 || (isComposite && !allowWholeKeyRead) ? null : "_id";
```

to:

```csharp
            path = isComposite && !allowWholeKeyRead ? null : "_id";
```

`isComposite` is always `false` when `keyParts.Count == 0` (see its computation at line 242: `keyParts.Count
== 0 ? false : ...`), so this makes a zero-part key resolve to `"_id"` unconditionally — the group's own
empty `_id: {}` document IS the correct value an empty anonymous-type key's `g.Key` should read back as.

Update the method's own doc comment (lines 352-356 and 371-377) to remove the "always [null] for a zero-part
(empty new{}) key" language — replace both occurrences of that phrase with a note that a zero-part key now
resolves to `"_id"` (the empty document) like any other key, and only a composite key with
`allowWholeKeyRead: false` still returns `null`.

- [ ] **Step 4: Fix `ResolveKeyMemberSerializationProperty`'s crash on an empty `keyParts` list**

At line 526-534, change:

```csharp
    private static IProperty? ResolveKeyMemberSerializationProperty(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite)
    {
        if (keyPath == "_id")
            return isComposite ? null : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

        var matchedPart = keyParts.First(p => keyPath == "_id." + p.Name);
        return (matchedPart.FieldRef as MongoFieldExpression)?.Property;
    }
```

to:

```csharp
    private static IProperty? ResolveKeyMemberSerializationProperty(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite)
    {
        // EF-322 SP7: a zero-part (empty new{}) key now also resolves keyPath == "_id" (TryGetKeyMemberPath's
        // own fix, this plan's Task 2) but has no single backing property at all — same "no property" answer
        // a composite key's own whole-key comparison already returns, for the same reason (no single field).
        if (keyPath == "_id")
            return isComposite || keyParts.Count == 0 ? null : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

        var matchedPart = keyParts.First(p => keyPath == "_id." + p.Name);
        return (matchedPart.FieldRef as MongoFieldExpression)?.Property;
    }
```

- [ ] **Step 5: Fix `TryBindGroupSideOperand`'s identical crash risk**

At line 1379-1389, change:

```csharp
        if (TryGetKeyMemberPath(side, groupingParameter, keyParts, isComposite, out var keyPath, allowWholeKeyRead: false))
        {
            if (keyPath == null)
                return false; // bare g.Key over a composite key — no single field to compare

            var matchedPart = isComposite ? keyParts.First(p => keyPath == "_id." + p.Name) : keyParts[0];
            keySerializationProperty = (matchedPart.FieldRef as MongoFieldExpression)?.Property;

            reference = new MongoElementRefExpression(keyPath, Unwrap(side).Type);
            return true;
        }
```

to:

```csharp
        if (TryGetKeyMemberPath(side, groupingParameter, keyParts, isComposite, out var keyPath, allowWholeKeyRead: false))
        {
            if (keyPath == null)
                return false; // bare g.Key over a composite key — no single field to compare

            // EF-322 SP7: a zero-part key (keyParts.Count == 0) resolves here too now (TryGetKeyMemberPath's
            // fix, this plan's Task 2) but has no single backing property — same as the composite case just
            // above it, for the same reason.
            keySerializationProperty = isComposite || keyParts.Count == 0
                ? null
                : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

            reference = new MongoElementRefExpression(keyPath, Unwrap(side).Type);
            return true;
        }
```

- [ ] **Step 6: Add a `KNOWN BUG` comment documenting the out-of-scope pre-existing gap**

In `TryResolveKeyReferenceAsRawExpression` (around line 668-703), immediately above the `if (member.Member.Name
== "Key" ...)` block (line 681), add:

```csharp
        // KNOWN BUG (EF-TBD, pre-existing, out of scope for EF-322 SP7): a zero-part (empty new{}) key's
        // g.Key referenced INSIDE an accumulator's own per-element condition (e.g. g.Count(e => g.Key ==
        // null)) reaches the `keyParts[0]` read below with an EMPTY keyParts list — IndexOutOfRangeException.
        // Unlike TryGetKeyMemberPath (fixed by SP7's Task 2), this method resolves against the per-element
        // RAW expression, never through TryGetKeyMemberPath, so SP7's fix does not reach it. No target test
        // exercises this shape; fix it if a future slice needs it.
```

- [ ] **Step 7: Run all four new/modified tests to confirm they pass**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_empty_key_bare_Key_readback_goes_native|FullyQualifiedName~GroupBy_empty_key_Where_on_bare_Key_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: PASS.

- [ ] **Step 8: Write unit tests pinning the two crash-guard fixes directly**

Add to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`:

1. A direct unit test calling `NativeGroupByBinder.TryBindGroupKey` with a zero-arg `new{}` key selector,
   then `NativeGroupByBinder.TryBindGroupWherePredicate` with a `g.Key == null` predicate lambda — assert it
   returns `true` (not a crash, not a decline) and inspect the resulting `PendingGroupPredicate` shape.
2. A direct unit test for the ordering carve-out: bind a zero-arg key, then confirm a `g.Key`-based
   `OrderBy` (via whatever internal entry point `NativeSlotPopulator`'s pending-ordering carve-out uses —
   read `TryBindGroupProjection`'s ordering-resolution block, lines 253-284, and the existing unit tests
   around it for the right call pattern) no longer declines.

Read the existing test file's fixture helpers first and mirror their pattern for both.

- [ ] **Step 9: Run the full unit test class**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests" \
  --logger "console;verbosity=normal"
```

Expected: PASS, including the two new tests.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: read a zero-part GroupBy key's bare g.Key back as the group's own empty _id document"
```

## Task 3: Verify — spec baselines, full suites, all EF versions

**Files:**
- Modify (baseline regeneration only, if `AssertMql` mismatches on content, not data):
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs`

**Interfaces:**
- Consumes: Task 1 + Task 2's production changes — this task is pure verification, no new production code.

- [ ] **Step 1: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

- [ ] **Step 2: Capture the FULL `NativeOnly` failure list BEFORE any further changes (baseline for the diff in Step 4)**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp7-before-nativeonly.log 2>&1
grep -E "^\s*Failed MongoDB" /tmp/sp7-before-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u > /tmp/sp7-before-names.txt
cat /tmp/sp7-before-names.txt
```

(This should already reflect Tasks 1 and 2's production changes if run after both — the "before" here is
before baseline regeneration/verification, not before this plan's own code changes.)

- [ ] **Step 3: Run the 2 target methods under default (`Native`) mode; regenerate baselines that changed**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_Dto_as_key_Select_Sum|FullyQualifiedName~GroupBy_empty_key_Aggregate_Key)" \
  --logger "console;verbosity=normal" > /tmp/sp7-native.log 2>&1
tail -60 /tmp/sp7-native.log
```

Both methods' current baselines were captured from the OLD driver-LINQ fallback pipeline — the native
pipeline this plan produces is likely to match closely (the existing baselines already show a `$group`+
`$project` shape) but may differ in field ordering or the accumulator's own field name (`__agg0` vs. `Sum`).
For any method whose `AssertMql` fails with `Assert.Equal() Failure: Strings differ` (a baseline mismatch, NOT
a data/behavior failure), regenerate:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm each diff shows a pipeline shape consistent with this plan's design (a `$group` keyed by the DTO's
member names for Task 1; a `$group` with `_id: {}` for Task 2 — unchanged from today, since the KEY side of
`GroupBy_empty_key_Aggregate_Key` already went native before this plan, only the `g.Key` READ side changes),
then rebuild and rerun WITHOUT the env var to confirm green.

- [ ] **Step 4: Run the same 2 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_Dto_as_key_Select_Sum|FullyQualifiedName~GroupBy_empty_key_Aggregate_Key)" \
  --logger "console;verbosity=normal" > /tmp/sp7-nativeonly.log 2>&1
tail -20 /tmp/sp7-nativeonly.log
```

Expected: both methods (4 with async) now PASS under `NativeOnly`.

- [ ] **Step 5: Re-measure the whole class's `NativeOnly` failure list, diffing against Step 2's capture**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp7-after-nativeonly.log 2>&1
grep -E "^\s*Failed MongoDB" /tmp/sp7-after-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u > /tmp/sp7-after-names.txt
diff /tmp/sp7-before-names.txt /tmp/sp7-after-names.txt
```

Expected: the diff shows exactly `GroupBy_Dto_as_key_Select_Sum` and `GroupBy_empty_key_Aggregate_Key`
disappearing (each with both `async: True` and `async: False` rows, if the log lists them separately), and
NOTHING else changing. If the diff differs by anything else, investigate before proceeding — either a genuine
bonus fix (confirm via `AssertMql` mismatch, not a data/behavior failure) or a sign this plan's change touched
something unintended.

- [ ] **Step 6: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp7-spec-full.log 2>&1
tail -8 /tmp/sp7-spec-full.log
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Run the full Functional suite (not just NativeGroupByTests/NativeGroupByCtorProjectionTests)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp7-functional-full.log 2>&1
tail -8 /tmp/sp7-functional-full.log
```

Expected: PASS, 0 failed.

- [ ] **Step 8: Run the full Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp7-unit-full.log 2>&1
tail -8 /tmp/sp7-unit-full.log
```

Expected: PASS, 0 failed.

- [ ] **Step 9: Run all three suites on EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" -v quiet
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" -v quiet

for CFG in EF8 EF9; do
  echo "=== $CFG unit ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug $CFG" --no-build 2>&1 | tail -5
  echo "=== $CFG spec (GroupBy class) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -5
  echo "=== $CFG functional (GroupBy) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests|FullyQualifiedName~NativeGroupByCtorProjectionTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all commands.

- [ ] **Step 10: Commit any baseline changes from Step 3**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
git commit -m "EF-322: verify SP7 DTO-key/empty-key-readback slice — baselines, full suites"
```

---

## After this plan

- File a follow-up ticket for `Odata_groupby_empty_key`'s nested-construction gap (see "Deviations from the
  spec" above) if one doesn't already exist — the design doc's SP8/SP9 sections should be re-verified against
  actual code the same way this plan re-verified SP7 before planning either of them (both add their own new
  accumulator/projection shapes that may have similarly stale gap descriptions).
- The `KNOWN BUG` comment added in Task 2 Step 6 (`TryResolveKeyReferenceAsRawExpression`'s zero-part-key
  crash inside an accumulator condition) has no ticket filed — worth doing before someone hits it cold, same
  as SP6's own precedent.
- **This branch tracks `origin/EF-322-Native-LINQ-rebased`, not `main`.** Ask the user before merging,
  pushing, or squashing anything.
