# Native GroupBy Nested-Construction Projection Member Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate a `GroupBy` Select-projection member whose value is itself a fresh `new`/object-initializer
construction (a nested `NewExpression`/`MemberInitExpression`, e.g. `Container = new LastInChain { Name = "x",
Value = g.Sum(...) }`) — moving the last SP7-descoped `NorthwindGroupByQueryMongoTest` method off the
driver-LINQ fallback: `Odata_groupby_empty_key`.

**Naming note:** the design doc's own SP8/SP9 slots are already claimed by unrelated features (group-elements-
as-a-list; GroupBy-over-projected-source). This slice is a standalone follow-on to SP7, not part of that
numbering — hence the descriptive (unnumbered) plan filename.

**Architecture:** SP7 already made `Odata_groupby_empty_key`'s KEY side natively representable (a zero-part
`new NoGroupByWrapper()` key, fixed by SP7 Task 2's `TryGetKeyMemberPath` change). The remaining gap is purely
in the projection: `Select(e => new NoGroupByAggregationWrapper { Container = new LastInChain { Name =
"TotalAmount", Value = e.Sum(e => (decimal)e.OrderID) } })` has ONE top-level binding (`Container`) whose value
is itself a `MemberInitExpression` with two of its OWN bindings — a plain string constant (`Name`) and an
accumulator call over the grouping parameter (`Value`). `NativeGroupByBinder.TryBindGroupProjection`'s
per-member loop currently tries exactly three shapes per binding value (`TryGetKeyMemberPath`,
`TryBindAccumulator`, `TryTranslateGroupProjectionExpression`'s computed-leaf dispatch) — none recognizes a
nested construction, so the whole query falls back.

Investigation this session found the general MECHANISM to represent a nested construction already exists,
shipped by EF-447 (an unrelated, already-closed ticket for an ordinary top-level Select over `VectorSearch`,
`Select(e => new { Book = new Book { Id = e.Id, ... }, Score = ... })`):

- `Expressions/MongoDocumentConstructionExpression.cs` — a generic node: `Members` is
  `IReadOnlyList<(string MemberName, MongoExpression Value)>`, NOT restricted to any particular `MongoExpression`
  subtype.
- `MongoAggregationExpressionRenderer`'s `Render`/`CanRender` arms for it (`NativeTranslation/
  MongoAggregationExpressionRenderer.cs:151,269`) are FULLY GENERIC — each member renders/is-checked through the
  SAME recursive `Render`/`CanRender` dispatch regardless of subtype. **No renderer change is needed** — a
  nested `MongoElementRefExpression` (an accumulator's flattened output, or a `g.Key`/`g.Key.Sub` reference) or a
  `MongoConstantExpression` already renders correctly inside a `MongoDocumentConstructionExpression` today.
- The READ side, however, is genuinely narrow: `MongoProjectionBindingRemovingExpressionVisitor
  .ReadDocumentConstructionMemberTyped` (`Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:940-955`)
  does `var field = (MongoFieldExpression)value;` — a HARD CAST. EF-447's own recognizer
  (`NativeProjectionBinder.TryGetDocumentConstructionLeaf`) only ever produces `MongoFieldExpression` members
  (by design — its scope is "every member must be a plain top-level scalar leaf"), so this cast has never
  needed to handle anything else. Any GroupBy-produced nested member (an accumulator's `MongoElementRefExpression`
  output, a key's `MongoElementRefExpression`, or a constant's `MongoConstantExpression`) would throw
  `InvalidCastException` here. **This is the one genuinely new piece of code this plan needs.**

The plan therefore has exactly two moving parts:

1. **A new GroupBy-side recognizer** (`NativeGroupByBinder`) that treats a projection member's value as a
   nested construction when it has projection members of its own (`Expression.TryGetProjectionMembers`),
   recursively resolving EACH nested member through the SAME three-way dispatch the outer per-member loop
   already uses (key access → accumulator → computed/constant), threading newly-allocated accumulator fields
   into the SAME `accumulators`/`flatten` lists the outer loop builds (an accumulator can only run inside
   `$group`, never inside the later `$project`'s literal sub-document — so a nested accumulator member still
   becomes its own top-level `$group` output field, referenced from inside the nested `MongoDocumentConstructionExpression`
   by a `MongoElementRefExpression` pointing at that top-level field, exactly like a top-level accumulator member
   already does). Recurses to support arbitrary nesting depth for free.
2. **A generalized read side**: `ReadDocumentConstructionMemberTyped` dispatches on whether the member's value
   is a `MongoFieldExpression` (existing field-aware path, needs `IProperty` for correct BSON representation) or
   anything else (a new property-free path read via the ALREADY-EXISTING `BsonBinding.CreateGetElementValueAtPath`
   — the exact same property-free mechanism a top-level bare/computed alias projection already uses today, at
   `MongoProjectionBindingRemovingExpressionVisitor.cs:204-206`, for reading a raw computed/accumulator alias
   with no backing `IProperty`).

No new `MongoExpression` subtype. No renderer change. No `MongoExpressionNodeCoverageTests.cs` row needed
(that matrix is keyed by node TYPE, and `MongoDocumentConstructionExpression` is already a covered type).

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$project`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (this slice is a
follow-on to the SP7 section; not itself numbered SP8/SP9, which are already claimed by other features).

## Global Constraints

- **`Native == DriverLinq` invariant** — `Odata_groupby_empty_key`'s `AssertQuery` must keep passing under
  `MongoQueryMode.Native` exactly as it does today (via fallback), in addition to newly passing under
  `MongoQueryMode.NativeOnly`.
- **No new `MongoExpression` subtype** — reuse `MongoDocumentConstructionExpression` exactly as EF-447 left it
  (its shape is already generic; do not change `Expressions/MongoDocumentConstructionExpression.cs`).
- **A value-converted or non-default-`BsonRepresentation` member must still decline, never silently
  mis-serialize.** This guard must come for free by reusing `TryGetKeyMemberPath`/`TryBindAccumulator`
  UNCHANGED for each nested member (both already enforce it for the top-level case) — do not write a parallel,
  possibly-weaker check for the nested case.
- **A nested member this plan doesn't recognize must decline the WHOLE outer projection**, not silently drop
  or partially translate the nested construction. Falling back to driver-LINQ is always safe; a partial native
  translation of a nested object is not.
- **This shape must never reach the "mixed" (late-fallback) read path.** `MongoMixedProjectionBindingRemovingExpressionVisitor`
  reads WHOLE, un-projected documents for leaves it couldn't fully resolve at translate time; an accumulator's
  output or a `g.Key` reference has NO natural root-relative document path (it exists only after `$group` runs)
  — there is nothing for that visitor to read from a raw stored document. Task 1 must verify (by tracing
  `MongoShapedQueryCompilingExpressionVisitor`'s gate, per Query/AGENTS.md's "post-terminal gating" invariant)
  that a `Grouping`-bearing `MongoSelectDefinition` is always all-native-or-all-fallback for its own projection
  (never partially mixed), so this case structurally cannot reach the mixed visitor. If that invariant does NOT
  already hold, this plan's scope must shrink to also add an explicit guard — do not assume; trace and confirm,
  and record the finding in the plan's own progress ledger either way.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.
- Preserve file BOMs.
- Re-run `NorthwindGroupByQueryMongoTest` under `MONGODB_EF_NATIVE_ONLY=1` after the task and recount the
  `NativeOnly` failure delta from the log (don't assume) — per the branch-review-coverage discipline every
  prior SP slice has followed.

## Review Focus

- **A nested member combining a per-element (non-key, non-accumulator) reference with the grouping parameter**
  (e.g. `Container = new LastInChain { Name = o.CustomerID, Value = ... }` where `o` is a raw per-element
  reference, not `g`) must decline — the recognizer's fallback to `TryTranslateGroupProjectionExpression`
  already declines via its own `ReferencesParameter` guard; add a test proving the WHOLE outer projection
  declines (not a partial/wrong nested read).
- **A doubly-nested construction** (three levels: `Outer { Mid = new Mid { Inner = new Inner { X = g.Sum(...) }
  } } }`) — the recognizer's own recursive call into itself for a nested member's value must handle this, since
  nothing in the design restricts recursion depth. Add one test at two levels of nesting beyond the target
  shape (the target itself is one level: outer wrapper + one nested `Container`).
- **Two nested accumulator members in the SAME construction** (`new LastInChain { A = g.Sum(...), B =
  g.Count() }`) — the synthetic top-level accumulator field name allocator must not collide between them, or
  with any sibling top-level accumulator/ordering-accumulator field the outer loop already allocates. Verify
  distinct synthetic names are produced and both accumulators land in `select.Grouping`'s accumulator list.
- **A nested member's own type requires a numeric widen/box** (`Odata_groupby_empty_key`'s own `Value` member
  is declared `object` but the accumulator produces `decimal`) — `ReadDocumentConstructionMemberTyped`'s
  existing `read.Type == memberType ? read : Expression.Convert(read, memberType)` fallback must correctly box
  the `decimal` into `object`; pin this with the actual target shape's own differential test, not just a
  same-typed member.
- **A nested construction used as a KEY-side value** (not projection) is explicitly OUT of this plan's scope —
  `TryBindKeyPartValue`'s existing Guard 1 (`NativeGroupByBinder.cs` around line 155-160) already declines a
  `MemberInitExpression` key-part value that resolves to `MongoDocumentConstructionExpression`; confirm (by
  reading, not by assumption) that this plan's changes don't accidentally loosen that guard, since the two
  code paths are separate but both touch the same node type.

---

## Task 1: Nested-construction GroupBy projection member

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`
  - `TryBindGroupProjection`'s per-member loop (currently ~lines 297-330 as of commit `31441f03`; confirm exact
    lines by reading the file, since line numbers drift between slices).
  - Add new private method `TryBindNestedGroupProjectionConstruction` (placed near `TryTranslateGroupProjectionExpression`,
    which it calls into for a nested leaf's own non-key/non-accumulator values).
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
  - `ReadDocumentConstructionMemberTyped` (~lines 940-955) and the new `ReadDocumentConstructionMemberGeneric`
    method this task adds beside it.
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs`
  (`Odata_groupby_empty_key` override — baseline regeneration only)

**Interfaces:**
- Consumes: `TryGetKeyMemberPath(Expression, ParameterExpression, IReadOnlyList<MongoGroupingKeyPart>, bool,
  out string?, bool allowWholeKeyRead = true)` (existing, unchanged) — resolves a `g.Key`/`g.Key.Sub` access.
- Consumes: `TryBindAccumulator(Expression, string, ParameterExpression, IReadOnlyList<MongoGroupingKeyPart>,
  bool, MongoExpressionTranslator, out MongoGroupAccumulator, out MongoExpression)` (existing, unchanged) —
  translates an accumulator call, allocating a top-level `$group` output field named by its `string` parameter.
- Consumes: `TryTranslateGroupProjectionExpression(Expression, ParameterExpression,
  IReadOnlyList<MongoGroupingKeyPart>, bool, MongoExpressionTranslator, out MongoExpression?)` (existing,
  unchanged) — the computed/ternary/coalesce/ordinary-value dispatch, called for a nested member that is
  neither a key access, an accumulator, nor a further nested construction.
- Consumes: `Expression.TryGetProjectionMembers(out IReadOnlyList<(string, Expression)>, bool
  allowPositionalConstructorArguments = ...)` (existing extension method, already used by the outer loop) — the
  SAME flattening helper, called recursively on a nested value.
- Produces: `TryBindNestedGroupProjectionConstruction(Expression expr, ParameterExpression groupingParameter,
  IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite, MongoExpressionTranslator translator,
  List<MongoGroupAccumulator> accumulators, ref int nestedAccumulatorCounter, out
  MongoDocumentConstructionExpression? result) : bool` — new. Threads the OUTER loop's own `accumulators` list
  (any nested accumulator's `MongoGroupAccumulator` is appended to the SAME list the outer loop already
  collects into `select.Grouping`) and a `ref int` counter for synthetic field-name uniqueness across however
  many nested-construction members the outer projection has.
- Produces: `ReadDocumentConstructionMemberGeneric(MongoDocumentConstructionExpression construction, string
  alias, string memberName, Type memberType) : Expression` — new, `protected virtual` (mirrors
  `ReadDocumentConstructionMember`'s own accessibility, in case a subclass ever needs to override it the same
  way `MongoMixedProjectionBindingRemovingExpressionVisitor` overrides the field-aware sibling — though per this
  plan's own Global Constraints, that override should never actually be reached for a GroupBy-produced member).

- [ ] **Step 1: Confirm the mixed-visitor reachability question (Global Constraints' own required trace)**

Before writing any code, trace `MongoShapedQueryCompilingExpressionVisitor`'s native/fallback classification
(`ClassifyNativeDisposition`, per `Query/AGENTS.md`'s pipeline diagram) for a `Grouping`-bearing
`MongoSelectDefinition`. Confirm: when `TryBindGroupProjection` returns `false` for ANY reason (including this
plan's own new nested-construction recognizer declining), does the ENTIRE query fall back to driver-LINQ (never
producing a partially-native, partially-mixed-read shape)? Read `MongoQueryableMethodTranslatingExpressionVisitor`'s
`GroupBy`/`Select`-after-`GroupBy` handling to see whether a `TryBindGroupProjection` failure is wired to
`MarkNotNativelyRepresentable()` (full fallback) rather than a partial/mixed disposition. Write your finding as
a one-paragraph comment at the top of `TryBindNestedGroupProjectionConstruction` (added in Step 3) citing the
exact call site that proves it. If the finding is NEGATIVE (a GroupBy projection CAN reach a mixed read today),
stop and escalate — this plan's scope would need to grow to also guard
`MongoMixedProjectionBindingRemovingExpressionVisitor`'s own `ReadDocumentConstructionMember` override, which is
not otherwise in scope here.

- [ ] **Step 2: Write a failing functional test proving the shape currently falls back**

Add to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`, inside the
`NativeGroupByTests` class:

```csharp
    private class NestedAggregateContainer
    {
        public string Name { get; set; } = "";
        public object Value { get; set; } = null!;
    }

    private class NestedAggregateWrapper
    {
        public NestedAggregateContainer Container { get; set; } = null!;
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_projection_member_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_nested_construction_projection_member_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 10 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 20 } },
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
        // here proves the nested construction went native.
        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedAggregateWrapper
            {
                Container = new NestedAggregateContainer
                {
                    Name = "TotalAmount",
                    Value = g.Sum(o => (decimal)o.OrderID)
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal("TotalAmount", result.Container.Name);
        Assert.Equal(30m, result.Container.Value);
    }
```

- [ ] **Step 3: Run it to confirm it fails today**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_select_with_nested_construction_projection_member_goes_native"
```

Expected: FAIL with `NativeTranslationNotSupportedException` (the shape still falls back).

- [ ] **Step 4: Add `TryBindNestedGroupProjectionConstruction` to `NativeGroupByBinder.cs`**

Add this new private method near `TryTranslateGroupProjectionExpression`:

```csharp
    // Recognizes a nested construction (a NewExpression/MemberInitExpression, e.g.
    // `Container = new LastInChain { Name = "x", Value = g.Sum(...) }`) as a projection member's own value.
    // Each of its OWN members is resolved through the SAME three-way dispatch the outer per-member loop uses
    // (key access, accumulator, computed/constant), recursing into this SAME method for a further-nested
    // construction. An accumulator found here allocates its own top-level $group output field — exactly like
    // a top-level accumulator member already does — since an accumulator can only run inside $group, never
    // inside a later $project's literal sub-document; `accumulators` is the SAME list the outer loop threads
    // into select.Grouping, and `nestedAccumulatorCounter` guarantees the synthetic field names this method
    // allocates never collide with each other or with the outer loop's own top-level member names.
    //
    // This shape never reaches the "mixed" (late-fallback) read path: replace this line with Step 1's own
    // trace finding, written as a permanent code comment citing the exact call site that proves a
    // Grouping-bearing MongoSelectDefinition is always all-native-or-all-fallback for its own projection.
    private static bool TryBindNestedGroupProjectionConstruction(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        List<MongoGroupAccumulator> accumulators,
        ref int nestedAccumulatorCounter,
        [NotNullWhen(true)] out MongoDocumentConstructionExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        if (!expr.TryGetProjectionMembers(out var nestedMembers))
            return false;

        var translatedMembers = new List<(string, MongoExpression)>();
        foreach (var (nestedMemberName, nestedValueRaw) in nestedMembers)
        {
            var nestedValue = Unwrap(nestedValueRaw);

            if (TryGetKeyMemberPath(nestedValue, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite/zero-part key inside a nested construction

                translatedMembers.Add((nestedMemberName, new MongoElementRefExpression(keyPath, nestedValue.Type)));
                continue;
            }

            var syntheticField = $"_nestedAgg{nestedAccumulatorCounter++}";
            if (TryBindAccumulator(
                    nestedValue, syntheticField, groupingParameter, keyParts, isComposite, translator,
                    out var acc, out var flattenRead))
            {
                accumulators.Add(acc);
                translatedMembers.Add((nestedMemberName, flattenRead));
                continue;
            }

            if (TryBindNestedGroupProjectionConstruction(
                    nestedValue, groupingParameter, keyParts, isComposite, translator, accumulators,
                    ref nestedAccumulatorCounter, out var deeperConstruction))
            {
                translatedMembers.Add((nestedMemberName, deeperConstruction));
                continue;
            }

            if (!TryTranslateGroupProjectionExpression(
                    nestedValue, groupingParameter, keyParts, isComposite, translator, out var computed))
                return false;

            translatedMembers.Add((nestedMemberName, computed));
        }

        result = new MongoDocumentConstructionExpression(expr, translatedMembers);
        return true;
    }
```

- [ ] **Step 5: Wire it into `TryBindGroupProjection`'s per-member loop**

In `TryBindGroupProjection` (`NativeGroupByBinder.cs`), find the per-member `foreach` loop over `bindings`
(currently structured as: try `TryGetKeyMemberPath`, then `TryBindAccumulator`, then fall to
`TryTranslateGroupProjectionExpression`). Declare a counter before the loop starts:

```csharp
        var nestedAccumulatorCounter = 0;
```

Then, inside the loop, insert a new attempt AFTER the existing `TryBindAccumulator` attempt and BEFORE the
existing `TryTranslateGroupProjectionExpression` fallback:

```csharp
            if (TryBindNestedGroupProjectionConstruction(
                    valueExpr, groupingParameter, keyParts, isComposite, translator, accumulators,
                    ref nestedAccumulatorCounter, out var construction))
            {
                flatten.Add(new MongoProjection(memberName, construction));
                continue;
            }
```

(placed immediately before the existing `// EF-322 SP4: a COMPUTED member value...` comment block and its
`TryTranslateGroupProjectionExpression` call, which remains as the final fallback, unchanged).

- [ ] **Step 6: Run the new functional test — confirm the binder now succeeds but the read side crashes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_select_with_nested_construction_projection_member_goes_native"
```

Expected: FAIL, but now with `InvalidCastException` (`Unable to cast object of type 'MongoElementRefExpression'
to type 'MongoFieldExpression'`) inside `ReadDocumentConstructionMemberTyped` — confirming translation now
succeeds and the read side is the remaining gap.

- [ ] **Step 7: Generalize `ReadDocumentConstructionMemberTyped` in `MongoProjectionBindingRemovingExpressionVisitor.cs`**

Replace:

```csharp
    private Expression ReadDocumentConstructionMemberTyped(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoExpression value,
        MemberInfo member)
    {
        var field = (MongoFieldExpression)value;
        var memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo fieldInfo => fieldInfo.FieldType,
            _ => field.Property.ClrType
        };

        var read = ReadDocumentConstructionMember(construction, alias, memberName, field, memberType);
        return read.Type == memberType ? read : Expression.Convert(read, memberType);
    }
```

with:

```csharp
    private Expression ReadDocumentConstructionMemberTyped(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoExpression value,
        MemberInfo member)
    {
        var memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo fieldInfo => fieldInfo.FieldType,
            _ => value is MongoFieldExpression fieldForType ? fieldForType.Property.ClrType : value.Type
        };

        // A plain top-level field has its own IProperty/serializer for correct BSON representation (EF-447's
        // own, narrower scope). Anything else here (an accumulator's flattened output, a g.Key/g.Key.Sub
        // reference, a nested construction, or a constant — all GroupBy-produced shapes EF-447 never needed to
        // handle) has no single backing property, so it reads back the SAME property-free way a top-level
        // bare/computed projection alias already does.
        var read = value is MongoFieldExpression field
            ? ReadDocumentConstructionMember(construction, alias, memberName, field, memberType)
            : ReadDocumentConstructionMemberGeneric(construction, alias, memberName, memberType);

        return read.Type == memberType ? read : Expression.Convert(read, memberType);
    }

    /// <summary>
    /// Reads a NON-field nested-construction member (an accumulator's own flattened output, a nested
    /// g.Key/g.Key.Sub reference, a further-nested construction, or an ordinary computed/constant value)
    /// generically by element path — the same property-free mechanism a top-level bare/computed projection
    /// alias already uses (<see cref="MongoDB.EntityFrameworkCore.Storage.BsonBinding.CreateGetElementValueAtPath"/>),
    /// since none of these member kinds has a single backing <see cref="IProperty"/>/serializer the field-aware
    /// <see cref="ReadDocumentConstructionMember"/> overload needs.
    /// </summary>
    protected virtual Expression ReadDocumentConstructionMemberGeneric(
        MongoDocumentConstructionExpression construction, string alias, string memberName, Type memberType)
        => BsonBinding.CreateGetElementValueAtPath(DocParameter, [alias, memberName], memberType);
```

- [ ] **Step 8: Run the functional test again — confirm it passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_select_with_nested_construction_projection_member_goes_native"
```

Expected: PASS.

- [ ] **Step 9: Add the Review Focus tests**

In `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`, add:

```csharp
    [Fact]
    public void GroupBy_select_with_nested_construction_referencing_per_element_value_declines_cleanly()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_nested_construction_referencing_per_element_value_declines_cleanly)));
        coll.InsertMany([new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 10 }, { "CustomerID", "A" } }]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var nativeOnlyDb = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // A per-element reference (o.CustomerID) mixed into the SAME nested construction as an accumulator —
        // the outer o is NOT the grouping parameter, so this must decline the WHOLE projection, not partially
        // translate it.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnlyDb.Entities
                .GroupBy(o => new { })
                .Select(g => new NestedAggregateWrapper
                {
                    Container = new NestedAggregateContainer
                    {
                        Name = g.First().CustomerID,
                        Value = g.Sum(o => (decimal)o.OrderID)
                    }
                })
                .AsEnumerable()
                .Single());
    }

    [Fact]
    public void GroupBy_select_with_doubly_nested_construction_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_doubly_nested_construction_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 10 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 20 } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new OuterNestedWrapper
            {
                Mid = new MidNestedWrapper
                {
                    Inner = new NestedAggregateContainer
                    {
                        Name = "Deep",
                        Value = g.Sum(o => (decimal)o.OrderID)
                    }
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal("Deep", result.Mid.Inner.Name);
        Assert.Equal(30m, result.Mid.Inner.Value);
    }

    private class OuterNestedWrapper
    {
        public MidNestedWrapper Mid { get; set; } = null!;
    }

    private class MidNestedWrapper
    {
        public NestedAggregateContainer Inner { get; set; } = null!;
    }

    // Nests BOTH accumulators inside the SAME construction (Container), not as two top-level Select members —
    // two SIBLING top-level accumulators already go through the outer per-member loop's own top-level dispatch
    // without ever reaching TryBindNestedGroupProjectionConstruction, so that shape would not exercise this
    // plan's own two-accumulator synthetic-field-naming path at all.
    [Fact]
    public void GroupBy_select_with_two_nested_accumulators_uses_distinct_synthetic_fields()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_two_nested_accumulators_uses_distinct_synthetic_fields)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 10 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OrderID", 20 } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new ThreeMemberNestedWrapper
            {
                Container = new TwoAccumulatorContainer
                {
                    Sum = g.Sum(o => (decimal)o.OrderID),
                    Count = g.Count()
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal(30m, result.Container.Sum);
        Assert.Equal(2, result.Container.Count);
    }

    private class ThreeMemberNestedWrapper
    {
        public TwoAccumulatorContainer Container { get; set; } = null!;
    }

    private class TwoAccumulatorContainer
    {
        public decimal Sum { get; set; }
        public int Count { get; set; }
    }
```

- [ ] **Step 10: Add unit tests in `NativeGroupByBinderTests.cs`**

Add tests directly against `NativeGroupByBinder.TryBindGroupProjection` (following the file's existing pattern
— build a `MongoQueryExpression`, call `TryBindGroupKey` for a zero-part key, then call `TryBindGroupProjection`
with a hand-built `LambdaExpression` for the nested-construction body) pinning:
- The target shape (`Container = new LastInChain { Name = "x", Value = g.Sum(...) }`) produces a
  `MongoProjection` whose `Expression` is a `MongoDocumentConstructionExpression` with exactly two `Members`,
  the first a `MongoConstantExpression` and the second a `MongoElementRefExpression` pointing at a synthetic
  `_nestedAgg0` field, and that `select.Grouping.Accumulators` contains the corresponding `MongoGroupAccumulator`.
- A nested member referencing a per-element (non-`g`) parameter returns `false` from `TryBindGroupProjection`
  (the whole-projection decline, not a partial result).
- Two sibling nested constructions in the SAME outer projection (two different top-level members, each with
  its own nested accumulator) produce distinct synthetic field names (no collision).

- [ ] **Step 11: Run the full unit and functional suites**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests"
```

Expected: all pass, 0 failures.

- [ ] **Step 12: Regenerate the `Odata_groupby_empty_key` spec baseline**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.Odata_groupby_empty_key"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.Odata_groupby_empty_key"
```

Expected: baseline diff shows a `$group`+`$project` pipeline with `Container` as a nested BSON document keyed
by `Name`/`Value`; rerun without the env var confirms green.

- [ ] **Step 13: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
  src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs \
  tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: translate a nested construction GroupBy projection member (Odata_groupby_empty_key)"
```

---

## Task 2: Verify — full suites, all EF versions

**Files:** none (verification only; commit only if a baseline needs a second-pass regeneration).

**Interfaces:**
- Consumes: Task 1's finished, reviewed commit — pure verification, no new production code.

- [ ] **Step 1: Capture the FULL `NativeOnly` failure list for `NorthwindGroupByQueryMongoTest`, before and after**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/nested-ctor-after-nativeonly.log 2>&1
grep -E "^\s*Failed MongoDB" /tmp/nested-ctor-after-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: `Odata_groupby_empty_key` no longer appears in the failure list; nothing else changed versus the
count this plan's own Task 1 dispatch recorded as the "before" state (the tip of the SP7 squash,
`31441f03`/`b441659a`).

- [ ] **Step 2: Run the full Specification, Functional, and Unit suites on EF10**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build
```

Expected: 0 new failures versus the known-good post-SP7 baseline (EF10 fully green; EF8/EF9 may show the 12
pre-existing, unrelated failures already confirmed present at the `origin/EF-322-Native-LINQ-rebased` tip
before this branch's own work — see the SP7 session's own verification for that list; this plan's change must
not add to it).

- [ ] **Step 3: Run all three suites on EF8 and EF9**

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

- [ ] **Step 4: Commit any baseline follow-up (if Step 1's numbers required a second regeneration pass)**

Only if needed — normally a no-op after Task 1's own Step 12.

---

## After this plan

- The `KNOWN BUG` comment in `TryResolveKeyReferenceAsRawExpression` (a zero-part-key crash reachable only via
  an accumulator's own per-element condition, e.g. `g.Count(e => g.Key == null)`) remains open, tracked
  separately, deliberately NOT touched by this plan.
- **This branch tracks `origin/EF-322-Native-LINQ-rebased`, not `main`.** Ask the user before merging, pushing,
  or squashing anything.
