# Native Join-Scope Nested Projection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Include_with_complex_projection` (and the general shape it represents — a nested
anonymous/DTO member whose value is itself a wrapped object sourced from a join scope) translate
natively instead of falling back to driver-LINQ.

**Architecture:** Extend `NativeJoinScopeProjectionBinder.TryBindProjection`'s per-member loop with a new
arm that recognizes a nested `NewExpression`/`MemberInitExpression` leaf, recursively translates its own
members via the existing `NativeJoinScopeTranslator.TryTranslateValue`, and packages the result into the
existing `MongoDocumentConstructionExpression` node (already built for EF-447, and already generic enough
to render and read a dotted/join-sourced field with no changes to the renderer or the native read side).
The one real gap is the mixed/fallback read leg
(`MongoMixedProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember`), which currently
assumes every member lives on the root document — it needs a dotted-path branch reusing the existing
`BsonBinding.CreateGetPropertyValueAtPath` helper.

**Tech Stack:** C#, EF Core provider internals (`Query/NativeTranslation`, `Query/Visitors`), xUnit with
plain `Assert.*`, MongoDB C# driver aggregation pipelines.

**Spec:** `docs/superpowers/specs/2026-09-08-native-join-scope-nested-projection-design.md`

## Global Constraints

- Single level of nesting only; a nested member that is itself nested, a whole-entity scope leaf, or an
  array/collection leaf declines the WHOLE outer leaf (no partial commit).
- Depth-1 join scope only (`scope.Levels.Count == 1`) — a chained join declines this leaf shape entirely.
- Do not modify `NativeProjectionBinder.TryGetDocumentConstructionLeaf`'s existing dotted-field decline —
  this is a second, separate recognizer.
- Do not add `MongoDocumentConstructionExpression` to the whole-entity-forces-sibling-readability allow-list
  (`NativeJoinScopeProjectionBinder.cs:332-336`) — mixing a nested leaf with a whole-entity sibling stays
  out of scope and must keep declining.
- The pre-existing undotted (EF-447 root-relative) read path in
  `MongoMixedProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember` must remain
  byte-for-byte unchanged.
- `src/` is nullable-enabled; annotate new code accordingly. Preserve file BOMs.
- Tests run serially (`DisableTestParallelization = true`) — don't add parallelism.

---

## File Structure

| File | Change |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs` | New recognizer arm inside `TryBindProjection`'s per-member loop. |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs` | Fix `ReadDocumentConstructionMember` to handle a dotted `field.ElementName`. |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeNestedProjectionTests.cs` | New: recognizer accept/decline matrix. |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitorTests.cs` | New (or extended if a file already covers this visitor): dotted vs. undotted `ReadDocumentConstructionMember` read-path test. |
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs` | Flip `Include_with_complex_projection`: drop the `// Failed:` comment, regenerate its MQL baseline. |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinScopeNestedProjectionTests.cs` | New: differential-correctness functional test (real DB) including an unmatched-FK case. |

---

### Task 1: Recognizer — nested wrapped leaf in `NativeJoinScopeProjectionBinder`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs:274` (insert new arm before the existing "ORDINARY (scalar/computed) leaf" arm, after the whole-entity scope-depth check at line 211-272)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeNestedProjectionTests.cs`

**Interfaces:**
- Consumes: `MongoJoinScope`/`MongoJoinScopeLevel` (`Query/Expressions/MongoJoinScope.cs`), `NativeJoinScopeTranslator.TryTranslateValue(MongoJoinScope, ParameterExpression, Expression, out MongoExpression?)` (`NativeJoinScopeTranslator.cs:39-42`), `ExpressionExtensionMethods.TryGetProjectionMembers(this Expression, out IReadOnlyList<(string, Expression)>)` (already used at `NativeJoinScopeProjectionBinder.cs:156` and `NativeProjectionBinder.cs:531`), `MongoDocumentConstructionExpression(Expression originalExpression, IReadOnlyList<(string MemberName, MongoExpression Value)> members)` (`Query/Expressions/MongoDocumentConstructionExpression.cs:44-50`), `MongoProjection(string Alias, MongoExpression Expression)`.
- Produces: `staged` list entries of shape `MongoProjection(alias, MongoDocumentConstructionExpression)`, consumed by the existing commit block (`NativeJoinScopeProjectionBinder.cs:338-344`) and, downstream, by `MongoShapedQueryCompilingExpressionVisitor.HasDocumentConstructionProjectionLeaf` (already matches on node type, no change needed) and the render/read sides (Task 2 only touches the mixed-fallback read).

Existing unit tests for this file live at
`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` — check for a
`NativeJoinScopeProjectionBinderTests.cs` or similar to match existing fixture-building conventions
(entity types, a `MongoJoinScope` builder helper, `LambdaExpression` construction via `Expression.Lambda`)
before writing the new test file; reuse whatever helper builds a `MongoQueryExpression` + `JoinInfo` +
`MongoJoinScope` for an existing ordinary-leaf test rather than inventing a second one.

- [ ] **Step 1: Read the existing test file(s) for this binder to find the fixture-building helpers**

Run: `grep -rl "NativeJoinScopeProjectionBinder\|TryBindProjection" tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`

Open whichever file(s) match and copy their pattern for constructing `MongoQueryExpression`, `JoinInfo`,
and a `LambdaExpression` selector over `Order`/`Customer` (or whatever entity pair that test file already
uses) — Order→Customer is the Northwind pair the motivating test uses, so prefer a fixture already wired
for that pair if one exists.

- [ ] **Step 2: Write the failing test for the motivating shape**

```csharp
[Fact]
public void Nested_nav_scalar_leaf_translates_to_document_construction_expression()
{
    // Arrange: build a MongoQueryExpression + registered JoinInfo/MongoJoinScope for
    // Order.Include(o => o.Customer)-shaped join scope (reuse this file's existing helper —
    // see whatever the ordinary-leaf-arm test uses to build `mongoQ` and `joinInfo`).
    var (mongoQ, joinInfo, scope, rootParam) = BuildOrderCustomerJoinScope();

    // selector: ti => new { CustomerId = new { Id = ti.Inner.CustomerID } }
    var selector = BuildNestedSelector(rootParam, scope);

    var result = NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo);

    Assert.True(result);
    var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "CustomerId");
    var construction = Assert.IsType<MongoDocumentConstructionExpression>(projection.Expression);
    var member = Assert.Single(construction.Members);
    Assert.Equal("Id", member.MemberName);
    var field = Assert.IsType<MongoFieldExpression>(member.Value);
    Assert.Contains('.', field.ElementName);
}
```

Fill in `BuildOrderCustomerJoinScope`/`BuildNestedSelector` using the exact helper patterns found in Step
1 — do not invent new entity-model plumbing if this file's sibling tests already have it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_nav_scalar_leaf_translates_to_document_construction_expression"`

Expected: FAIL — `TryBindProjection` returns `false` (or the assertion on `mongoQ.Select.Projection` fails
because nothing was staged).

- [ ] **Step 3: Implement the recognizer arm**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs`, inside
`TryBindProjection`'s `foreach (var (alias, leafBody) in members)` loop, insert the new arm immediately
after the closing `continue;` of the whole-entity scope-depth `if` block (currently ending at line 272)
and before the `// ORDINARY (scalar/computed) leaf.` comment (currently at line 274):

```csharp
            // A NESTED wrapped leaf (`CustomerId = new { Id = o.Customer!.CustomerID }`) — one level of
            // nesting only (native-join-scope-nested-projection ticket). Declines the WHOLE outer leaf (not
            // just this member) on any inner shape this doesn't recognize, exactly as
            // NativeProjectionBinder.TryGetDocumentConstructionLeaf does for its own plain-root nested
            // leaves — this is a SEPARATE recognizer building the same MongoDocumentConstructionExpression
            // node, not a relaxation of that one's dotted-field decline.
            if (scope.Levels.Count == 1
                && leafBody.TryGetProjectionMembers(out var nestedMembers))
            {
                var translatedNestedMembers = new List<(string, MongoExpression)>();
                var seenNestedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declined = false;

                foreach (var (nestedMemberName, nestedValue) in nestedMembers)
                {
                    if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, nestedValue, out var nestedLeaf)
                        || !seenNestedMembers.Add(nestedMemberName))
                    {
                        declined = true;
                        break;
                    }

                    translatedNestedMembers.Add((nestedMemberName, nestedLeaf));
                }

                if (declined)
                {
                    return false;
                }

                if (!seenAliases.Add(alias))
                {
                    return false;
                }

                staged.Add(new MongoProjection(
                    alias, new MongoDocumentConstructionExpression(leafBody, translatedNestedMembers)));
                continue;
            }

```

Add the necessary `using MongoDB.EntityFrameworkCore.Query.Expressions;` if `MongoDocumentConstructionExpression`
isn't already imported in this file (check the existing `using` block first).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_nav_scalar_leaf_translates_to_document_construction_expression"`

Expected: PASS

- [ ] **Step 5: Write the decline-matrix tests**

Add to the same test file:

```csharp
[Fact]
public void Double_nested_leaf_declines_whole_projection()
{
    var (mongoQ, joinInfo, scope, rootParam) = BuildOrderCustomerJoinScope();
    // selector: ti => new { A = new { B = new { C = ti.Inner.CustomerID } } }
    var selector = BuildDoubleNestedSelector(rootParam, scope);

    Assert.False(NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo));
    Assert.Empty(mongoQ.Select.Projection);
}

[Fact]
public void Nested_leaf_containing_whole_entity_member_declines_whole_projection()
{
    var (mongoQ, joinInfo, scope, rootParam) = BuildOrderCustomerJoinScope();
    // selector: ti => new { A = new { Whole = ti.Inner } }
    var selector = BuildNestedWholeEntitySelector(rootParam, scope);

    Assert.False(NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo));
    Assert.Empty(mongoQ.Select.Projection);
}

[Fact]
public void Chained_join_scope_declines_nested_leaf()
{
    var (mongoQ, joinInfo, scope, rootParam) = BuildTwoLevelJoinScope(); // scope.Levels.Count == 2
    var selector = BuildNestedSelector(rootParam, scope);

    Assert.False(NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo));
}

[Fact]
public void Nested_leaf_alongside_sibling_scalar_leaf_both_stage()
{
    var (mongoQ, joinInfo, scope, rootParam) = BuildOrderCustomerJoinScope();
    // selector: ti => new { OrderId = ti.Outer.OrderID, CustomerId = new { Id = ti.Inner.CustomerID } }
    var selector = BuildMixedSiblingSelector(rootParam, scope);

    Assert.True(NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo));
    Assert.Equal(2, mongoQ.Select.Projection.Count);
}

[Fact]
public void Nested_leaf_alongside_whole_entity_sibling_leaf_declines()
{
    var (mongoQ, joinInfo, scope, rootParam) = BuildOrderCustomerJoinScope();
    // selector: ti => new { Whole = ti.Inner, CustomerId = new { Id = ti.Inner.CustomerID } }
    var selector = BuildWholeEntityPlusNestedSelector(rootParam, scope);

    Assert.False(NativeJoinScopeProjectionBinder.TryBindProjection(mongoQ, selector, joinInfo));
}
```

Build each `Build*Selector` helper by adapting `BuildNestedSelector` from Step 1 — same
`TransparentIdentifier<TOuter,TInner>`-shaped parameter, different lambda body. Use `Expression.Lambda`
directly over hand-built `NewExpression`s if this test file already does that elsewhere (check before
introducing a query-syntax-based builder).

- [ ] **Step 6: Run all new tests to verify the matrix**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeJoinScopeNestedProjectionTests"`

Expected: PASS (all 6 tests)

- [ ] **Step 7: Run the full existing `NativeJoinScopeProjectionBinder`/`NativeJoinScopeTranslator` test suites to confirm no regression**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~JoinScope"`

Expected: PASS, same count as before this task plus the 6 new tests.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeJoinScopeNestedProjectionTests.cs
git commit -m "EF-TBD: native translation for a nested wrapped projection leaf sourced from a join scope"
```

---

### Task 2: Fix the mixed-visitor's dotted-field read for `MongoDocumentConstructionExpression`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs:330-341`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitorTests.cs`

**Interfaces:**
- Consumes: `MongoDocumentConstructionExpression` (Task 1's output), `MongoFieldExpression.ElementName`
  (`Query/Expressions/MongoFieldExpression.cs`), `BsonBinding.CreateGetPropertyValueAtPath(Expression
  bsonDocExpression, string[] path, IProperty property, Type mappedType)` (`Storage/BsonBinding.cs:255-263`).
- Produces: a corrected `Expression` tree for the mixed (fallback) read leg; consumed at execution time
  by the shaper when `MongoShapedQueryCompilingExpressionVisitor.HasDocumentConstructionProjectionLeaf`
  triggers the mixed visitor on a mid-compile `TryBuildNativeFactory` decline.

Before writing new tests, check whether `MongoMixedProjectionBindingRemovingExpressionVisitor` (or its
base `MongoProjectionBindingRemovingExpressionVisitor`) already has a unit test file exercising
`ReadDocumentConstructionMember`/`BuildDocumentConstructionExpression` directly (search first — EF-447
likely added one). If found, extend it instead of creating a new file, matching its existing
fixture-building style (a hand-built `MongoQueryExpression`, a `ParameterExpression` for `_docParameter`,
and a `MongoFieldExpression` constructed directly rather than via full query translation).

- [ ] **Step 1: Search for existing coverage**

Run: `grep -rl "ReadDocumentConstructionMember\|BuildDocumentConstructionExpression" tests/MongoDB.EntityFrameworkCore.UnitTests/`

- [ ] **Step 2: Write the failing test for the dotted-path case**

If an existing file covers the undotted case, add alongside it; otherwise create the new file. Either
way, the dotted-path test:

```csharp
[Fact]
public void ReadDocumentConstructionMember_uses_dotted_path_reader_for_join_scope_sourced_field()
{
    // Arrange: a MongoDocumentConstructionExpression whose one member's Value is a MongoFieldExpression
    // with a dotted ElementName ("_lookup_Customer.CustomerID"), matching what Task 1's recognizer stages.
    var customerIdProperty = /* IProperty for Customer.CustomerID — reuse this test file's existing
                                 model-building helper, or MongoModelBuildingTests-style in-memory model,
                                 whichever this project's Visitors unit tests already use */;
    var field = new MongoFieldExpression(customerIdProperty, "_lookup_Customer.CustomerID");
    var construction = new MongoDocumentConstructionExpression(
        Expression.New(typeof(object).GetConstructor(Type.EmptyTypes)!),
        [("Id", field)]);

    var visitor = /* construct MongoMixedProjectionBindingRemovingExpressionVisitor via this file's
                      existing helper */;

    var read = visitor.CallReadDocumentConstructionMember(construction, "CustomerId", "Id", field, typeof(string));

    // Assert the produced expression is a call to BsonBinding.CreateGetPropertyValueAtPath's underlying
    // GetPropertyValueAtPath<T> method (multi-segment reader), not the single-property GetValueExpression path.
    var call = Assert.IsType<MethodCallExpression>(read);
    Assert.Equal("GetPropertyValueAtPath", call.Method.Name);
}

[Fact]
public void ReadDocumentConstructionMember_keeps_single_property_reader_for_root_relative_field()
{
    var idProperty = /* IProperty for a root-relative field, e.g. Book.Id */;
    var field = new MongoFieldExpression(idProperty, "Id"); // undotted
    var construction = new MongoDocumentConstructionExpression(
        Expression.New(typeof(object).GetConstructor(Type.EmptyTypes)!),
        [("Id", field)]);

    var visitor = /* same helper as above */;

    var read = visitor.CallReadDocumentConstructionMember(construction, "Book", "Id", field, typeof(string));

    var call = Assert.IsType<MethodCallExpression>(read);
    Assert.NotEqual("GetPropertyValueAtPath", call.Method.Name);
}
```

`ReadDocumentConstructionMember` is `protected` — if this test file doesn't already have a way to invoke a
protected member (a test-only subclass or reflection helper), check
`MongoProjectionBindingRemovingExpressionVisitor`'s existing unit tests for the pattern already used for
its other `protected`/`internal` surface before inventing a new one.

- [ ] **Step 3: Run tests to verify the dotted-path one fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~ReadDocumentConstructionMember"`

Expected: `ReadDocumentConstructionMember_uses_dotted_path_reader_for_join_scope_sourced_field` FAILS
(current code always calls `CreateGetValueExpression`, never `GetPropertyValueAtPath`);
`ReadDocumentConstructionMember_keeps_single_property_reader_for_root_relative_field` PASSES already.

- [ ] **Step 4: Implement the fix**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`,
replace the body of `ReadDocumentConstructionMember` (lines 330-341):

```csharp
    protected override Expression ReadDocumentConstructionMember(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
        Type memberType)
    {
        var docExpr = (Expression)_docParameter;
        if (_queryExpression.UsesDriverJoinFields)
        {
            docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
        }

        // A join-scope-sourced member (native-join-scope-nested-projection ticket): field.ElementName is a
        // dotted path relative to docExpr (e.g. "_lookup_Customer.CustomerID"), not a root property of the
        // outer entity — CreateGetValueExpression(docExpr, field.Property, memberType) would incorrectly
        // look up field.Property's OWN element name directly on docExpr. Walk the dotted path instead via
        // the same multi-segment helper the ordinary (native) read side uses for its own [alias, memberName]
        // path — it already treats an absent INTERMEDIATE segment (an unmatched left-outer join row) as
        // null rather than a missing-leaf error, which is exactly what an Inner-side member needs here.
        if (field.ElementName.Contains('.'))
        {
            return BsonBinding.CreateGetPropertyValueAtPath(docExpr, field.ElementName.Split('.'), field.Property, memberType);
        }

        return CreateGetValueExpression(docExpr, field.Property, memberType);
    }
```

Add `using MongoDB.EntityFrameworkCore.Storage;` if `BsonBinding` isn't already imported in this file.

- [ ] **Step 5: Run tests to verify both pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~ReadDocumentConstructionMember"`

Expected: PASS (both tests)

- [ ] **Step 6: Run the full Visitors unit test suite to confirm no regression**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Visitors"`

Expected: PASS, same count as before plus the new test(s).

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitorTests.cs
git commit -m "EF-TBD: mixed-visitor reads a dotted document-construction member via its full path"
```

---

### Task 3: Differential-correctness functional test (real DB)

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinScopeNestedProjectionTests.cs`

**Interfaces:**
- Consumes: whatever this test project's existing Northwind-style or ad hoc fixture provides for
  `Order`/`Customer` with a reference navigation (check `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`
  for an existing fixture with an Order→Customer or equivalent reference-nav pair and an in-memory LINQ
  oracle helper — reuse it; if none exists, check `NativeModeAssert` in
  `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/` for the shared differential-assert
  pattern this repo uses elsewhere for native-shape correctness).
- Produces: nothing consumed by later tasks — this is a leaf verification task.

- [ ] **Step 1: Find the fixture and oracle-assert pattern to reuse**

Run: `grep -rl "NativeModeAssert" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ | head -5`

Open one of the matches and copy its pattern: a `[Theory]` seeding matched and unmatched rows, executing
the same `Expression` against an in-memory `List<T>.AsQueryable()` oracle and against the real MongoDB
context, and asserting the two produce equal results.

- [ ] **Step 2: Write the failing differential test**

```csharp
public class NativeJoinScopeNestedProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Nested_projection_over_reference_include_matches_oracle(MongoQueryMode mode)
    {
        var collectionName = database.CreateCollectionName(nameof(Nested_projection_over_reference_include_matches_oracle));
        using var context = /* this file's/this project's standard SetupContext(collectionName, mode) helper —
                                match whatever NativeModeAssert or a sibling native-shape test class already uses */;

        context.Set<Order>().AddRange(
            new Order { Id = 1, CustomerId = "ALFKI" },   // matched
            new Order { Id = 2, CustomerId = "MISSING" }); // unmatched FK — no such Customer
        context.Set<Customer>().Add(new Customer { Id = "ALFKI", Name = "Alfreds" });
        context.SaveChanges();

        Expression<Func<Order, object>> selector = o => new { CustomerId = new { Id = o.Customer!.CustomerID } };

        var actual = context.Set<Order>().Include(o => o.Customer).Select(selector).OrderBy(x => x).ToList();
        var oracle = new[] { new Order { Id = 1, CustomerId = "ALFKI" }, new Order { Id = 2, CustomerId = "MISSING" } }
            .AsQueryable().Select(selector).OrderBy(x => x).ToList();

        Assert.Equal(oracle.Count, actual.Count);
        // compare via reflection/dynamic since the projected type is anonymous — match whatever this
        // repo's existing anonymous-projection differential tests already use for the comparison (check
        // NativeArrayProjectionTests or NativeOwnedCollectionCountTests for the established helper).
    }
}
```

Adapt entity/property names (`Order`, `Customer`, `CustomerId`/`CustomerID`) to whatever this test
project's actual Northwind-style model under `FunctionalTests/` already defines — do not invent a new
model if a compatible one exists; check `tests/MongoDB.EntityFrameworkCore.FunctionalTests/` for the
existing `Order`/`Customer` types used by other reference-Include tests first.

- [ ] **Step 3: Run the test to verify it fails under `NativeOnly` before Tasks 1-2, and confirm it now passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_projection_over_reference_include_matches_oracle"`

Expected (after Tasks 1-2 are already committed, since this task runs after them): PASS for both
`Native` and `NativeOnly`.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinScopeNestedProjectionTests.cs
git commit -m "EF-TBD: differential-correctness test for nested join-scope projection, incl. unmatched FK"
```

---

### Task 4: Flip the spec test and regenerate its MQL baseline

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs:379-387`

**Interfaces:**
- Consumes: the shipped Tasks 1-2 behavior.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Remove the stale comment and re-run to regenerate the baseline**

Edit `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs`,
deleting line 381 (`// Failed: Throws ExpressionNotSupportedException (query not translated)`) from
`Include_with_complex_projection`.

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_complex_projection"
```

Expected: the test reports FAILED (that is the rewrite signal, per the SpecificationTests `AGENTS.md`) —
inspect the diff in `NorthwindIncludeQueryMongoTest.cs` afterward with `git diff` to confirm the rewritten
`AssertMql(...)` body looks like a genuine `$project`-based native pipeline (should contain a `$lookup`
into `Customers` plus a `$project` whose value for the `CustomerId` field is a nested document, not the
old fallback's `$map`/`$cond`/`$unwind` reshaping) rather than something obviously wrong (e.g. unchanged,
or truncated — the rewriter truncates at 9 statements).

- [ ] **Step 2: Rebuild and re-run without the rewrite flag to confirm genuinely green**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_complex_projection"
```

Expected: PASS (both `async: true` and `async: false`).

- [ ] **Step 3: Confirm under `NativeOnly`**

Run:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_complex_projection"
```

Expected: PASS — this is the proof the query now genuinely goes native (MQL shape alone can't prove it,
per `Query/AGENTS.md`).

- [ ] **Step 4: Repeat for EF8 and EF9**

Run (once per configuration):
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF8" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_complex_projection"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF8" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_complex_projection"
```

Repeat identically for `Debug EF9`. If any configuration's baseline differs from EF10's (check via `git
diff` after each rewrite), that is real information — do not force them to match; each configuration's
baseline is independently regenerated and independently verified green. Do not assume symmetry across
EF8/EF9/EF10 per this repo's own stated testing discipline (see the EF-449 spec's precedent, "check EF8
separately... confirm empirically, don't assume symmetry").

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs
git commit -m "EF-TBD: flip Include_with_complex_projection to native, regenerate MQL baseline"
```

---

### Task 5: Full-suite regression pass

**Files:** none (verification only)

**Interfaces:** none

- [ ] **Step 1: Run the full unit test suite for EF10**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build`

Expected: PASS, no new failures.

- [ ] **Step 2: Run the full SpecificationTests suite for EF10, both `Native` (default) and `NativeOnly`**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
```

Expected: no new failures relative to the pre-task baseline (the target test now passes; nothing else
should have flipped from pass to fail — a flip anywhere else means the new recognizer arm or the mixed-
visitor fix over-widened, per this repo's own "a gate wider than what the rewrite handles is a silent-
wrong-data trap" invariant, and needs investigation before proceeding).

- [ ] **Step 3: Run the full FunctionalTests suite for EF10**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build`

Expected: PASS, no new failures.

- [ ] **Step 4: Invoke `/test-all` (or run EF8/EF9 configurations manually) for full multi-version confirmation**

Run the `/test-all` skill, or manually:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
```

Expected: PASS across all three configurations.

- [ ] **Step 5: No commit for this task** (verification only — if anything is found broken, return to the
  relevant earlier task, fix, and re-commit there).

## Self-Review Notes

- **Spec coverage:** Design §1 (recognizer) → Task 1. Design §2 (no renderer/native-read change, verified
  by construction) → covered implicitly by Task 1's tests asserting the staged node renders/reads via the
  pre-existing generic paths (no separate task needed — there is nothing to implement). Design §3 (mixed
  read fix) → Task 2. Testing section's spec-test flip → Task 4. Testing section's differential-correctness
  functional test → Task 3. Full-suite regression → Task 5.
- **Out-of-scope boundary from the spec** (nested leaf + whole-entity sibling declines) is exercised by
  Task 1 Step 5's `Nested_leaf_alongside_whole_entity_sibling_leaf_declines` test — confirms the boundary
  holds rather than being merely asserted in prose.
- **Placeholder scan:** every code step above contains complete, compilable-shape C#; the only intentionally
  open items are fixture-helper *names*, which are explicitly directed to be copied from this repo's own
  existing sibling test files (named by search command) rather than invented — this is a "find the existing
  pattern" instruction, not a "figure out what to do" placeholder.
- **Type consistency:** `MongoDocumentConstructionExpression(Expression, IReadOnlyList<(string, MongoExpression)>)`,
  `NativeJoinScopeTranslator.TryTranslateValue(MongoJoinScope, ParameterExpression, Expression, out
  MongoExpression?)`, and `BsonBinding.CreateGetPropertyValueAtPath(Expression, string[], IProperty, Type)`
  are used identically (same parameter order/types) in both the spec and every task that references them.
